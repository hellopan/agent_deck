package agentfind

import (
	"os"
	"path/filepath"
	"regexp"
	"sort"
	"strings"
	"unicode/utf16"
)

// shortcutTarget reads the absolute executable path out of a Windows .lnk file.
//
// Parsing the full shell link structure is unnecessary for detection, and the
// layout differs between shortcuts written by installers and by the shell. Both
// however store the target path verbatim, once as ASCII and once as UTF-16, so
// scanning for a drive-letter path finds it in either form. This is deliberately
// read-only and never launches anything.
func shortcutTarget(data []byte) string {
	if len(data) == 0 {
		return ""
	}
	ascii := strings.ToLower(string(data))
	wide := strings.ToLower(decodeUTF16LE(data))
	for _, text := range []string{ascii, wide} {
		for _, match := range executablePattern.FindAllString(text, -1) {
			match = strings.TrimPrefix(match, `\\?\`)
			return match
		}
	}
	return ""
}

var executablePattern = regexp.MustCompile(`[a-z]:\\(?:[^\\/:*?"<>|\x00-\x1f]{1,80}\\){0,12}[^\\/:*?"<>|\x00-\x1f]{1,80}\.exe`)

func decodeUTF16LE(data []byte) string {
	if len(data)%2 != 0 {
		data = data[:len(data)-1]
	}
	units := make([]uint16, len(data)/2)
	for i := range units {
		units[i] = uint16(data[2*i]) | uint16(data[2*i+1])<<8
	}
	return string(utf16.Decode(units))
}

// shortcutFiles lists the .lnk files whose name mentions a tool. Scanning the
// start menu is what makes detection survive installs outside Program Files,
// which is exactly where this project's own tools live.
func shortcutFiles(dir, name string) []string {
	var found []string
	entries, err := os.ReadDir(dir)
	if err != nil {
		return nil
	}
	for _, entry := range entries {
		full := filepath.Join(dir, entry.Name())
		if entry.IsDir() {
			found = append(found, shortcutFiles(full, name)...)
			continue
		}
		if strings.EqualFold(filepath.Ext(entry.Name()), ".lnk") &&
			strings.Contains(strings.ToLower(entry.Name()), strings.ToLower(humanName(name))) {
			found = append(found, full)
		}
	}
	sort.Strings(found)
	return found
}
