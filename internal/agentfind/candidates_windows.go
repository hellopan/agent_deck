//go:build windows

package agentfind

import (
	"os"
	"path/filepath"
	"sort"
	"strings"

	"golang.org/x/sys/windows"
	"golang.org/x/sys/windows/registry"
)

// candidates lists every place worth looking for a tool, most trustworthy
// first. The order matters: the first usable hit wins.
func candidates(name string) []candidate {
	var places []candidate
	if name == Codex {
		// The installed build is the only one that can run shell tools, so it
		// is checked before anything a registry entry might point at.
		places = append(places, installedCodex()...)
	}
	places = append(places, registryEntries(name)...)
	places = append(places, startMenuShortcuts(name)...)
	places = append(places, installFolders(name)...)
	places = append(places, pathLookup(name)...)
	if name == Codex {
		// Last resort: the stripped sandbox copy. It is kept in the list only
		// so that "Codex is installed" can be reported together with the reason
		// it cannot be used, instead of a bare "not found".
		if home, err := os.UserHomeDir(); err == nil {
			places = append(places, candidate{filepath.Join(home, ".codex", ".sandbox-bin", "codex.exe"), "sandbox-bin"})
		}
	}
	return places
}

// installedCodex finds the per-build directory Codex desktop unpacks into and
// returns them newest first.
func installedCodex() []candidate {
	base := filepath.Join(os.Getenv("LOCALAPPDATA"), "OpenAI", "Codex", "bin")
	builds, err := os.ReadDir(base)
	if err != nil {
		return nil
	}
	type build struct {
		path string
		when int64
	}
	var found []build
	for _, entry := range builds {
		if !entry.IsDir() {
			continue
		}
		exe := filepath.Join(base, entry.Name(), "codex.exe")
		info, err := os.Stat(exe)
		if err != nil || info.IsDir() {
			continue
		}
		found = append(found, build{exe, info.ModTime().UnixNano()})
	}
	sort.Slice(found, func(i, j int) bool { return found[i].when > found[j].when })
	places := make([]candidate, 0, len(found))
	for _, item := range found {
		places = append(places, candidate{item.path, "installed"})
	}
	return places
}

var uninstallRoots = []string{
	`SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall`,
	`SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall`,
}

// registryEntries uses Add/Remove Programs records. This is what finds tools
// installed outside Program Files, and it survives a renamed folder.
func registryEntries(name string) []candidate {
	match := strings.ToLower(humanName(name))
	exe := executableName(name)
	roots := []registry.Key{registry.LOCAL_MACHINE, registry.CURRENT_USER}
	var places []candidate
	for _, root := range roots {
		for _, path := range uninstallRoots {
			key, err := registry.OpenKey(root, path, registry.READ)
			if err != nil {
				continue
			}
			children, err := key.ReadSubKeyNames(-1)
			if err != nil {
				key.Close()
				continue
			}
			for _, child := range children {
				entry, err := registry.OpenKey(key, child, registry.READ)
				if err != nil {
					continue
				}
				display, _, _ := entry.GetStringValue("DisplayName")
				if !strings.Contains(strings.ToLower(display), match) {
					entry.Close()
					continue
				}
				if found := entryExecutable(entry, exe); found != "" {
					places = append(places, candidate{found, "registry"})
				}
				entry.Close()
			}
			key.Close()
		}
	}
	return places
}

func entryExecutable(entry registry.Key, exe string) string {
	if location, _, err := entry.GetStringValue("InstallLocation"); err == nil && location != "" {
		base := strings.Trim(strings.TrimSpace(location), `"`)
		if candidate := findUnder(base, exe); candidate != "" {
			return candidate
		}
	}
	icon, _, err := entry.GetStringValue("DisplayIcon")
	if err != nil || icon == "" {
		return ""
	}
	icon = strings.Trim(strings.TrimSpace(icon), `"`)
	if index := strings.LastIndex(icon, ","); index > 2 {
		icon = icon[:index]
	}
	if strings.EqualFold(filepath.Base(icon), exe) {
		return icon
	}
	return ""
}

// findUnder looks for an executable at the root and one level down, which
// covers both "install dir holds the exe" and "install dir holds a versioned
// subdirectory".
func findUnder(base, exe string) string {
	if base == "" {
		return ""
	}
	direct := filepath.Join(base, exe)
	if info, err := os.Stat(direct); err == nil && !info.IsDir() {
		return direct
	}
	children, err := os.ReadDir(base)
	if err != nil {
		return ""
	}
	for _, child := range children {
		if !child.IsDir() {
			continue
		}
		nested := filepath.Join(base, child.Name(), exe)
		if info, err := os.Stat(nested); err == nil && !info.IsDir() {
			return nested
		}
	}
	return ""
}

func startMenuShortcuts(name string) []candidate {
	dirs := []string{
		filepath.Join(os.Getenv("ProgramData"), `Microsoft\Windows\Start Menu\Programs`),
		filepath.Join(os.Getenv("APPDATA"), `Microsoft\Windows\Start Menu\Programs`),
	}
	var places []candidate
	for _, dir := range dirs {
		for _, file := range shortcutFiles(dir, name) {
			data, err := os.ReadFile(file)
			if err != nil {
				continue
			}
			if target := shortcutTarget(data); target != "" {
				places = append(places, candidate{target, "shortcut"})
			}
		}
	}
	return places
}

func installFolders(name string) []candidate {
	exe := executableName(name)
	roots := []string{
		os.Getenv("ProgramFiles"),
		os.Getenv("ProgramFiles(x86)"),
		filepath.Join(os.Getenv("LOCALAPPDATA"), "Programs"),
	}
	for _, drive := range fixedDrives() {
		roots = append(roots, drive+`:\Program Files`, drive+`:\Program Files (x86)`)
	}
	names := []string{humanName(name)}
	if humanName(name) != "Codex" {
		names = append(names, strings.ToLower(humanName(name)))
	}
	var places []candidate
	for _, root := range roots {
		if root == "" {
			continue
		}
		for _, folder := range names {
			places = append(places, candidate{filepath.Join(root, folder, exe), "folder"})
		}
	}
	return places
}

func fixedDrives() []string {
	mask, err := windows.GetLogicalDrives()
	if err != nil {
		return nil
	}
	var drives []string
	for index := 0; index < 26; index++ {
		if mask&(1<<uint(index)) == 0 {
			continue
		}
		letter := string(rune('A' + index))
		// Only fixed disks: removable and network drives cost a timeout each
		// and never hold a development tool.
		if kind := windows.GetDriveType(windows.StringToUTF16Ptr(letter + `:\`)); kind != windows.DRIVE_FIXED {
			continue
		}
		drives = append(drives, letter)
	}
	return drives
}

func pathLookup(name string) []candidate {
	exe := executableName(name)
	for _, dir := range filepath.SplitList(os.Getenv("PATH")) {
		if dir == "" {
			continue
		}
		full := filepath.Join(dir, exe)
		if info, err := os.Stat(full); err == nil && !info.IsDir() {
			return []candidate{{full, "path"}}
		}
	}
	return nil
}
