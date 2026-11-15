//go:build !windows

package projects

import "os"

func redirectedDirectory(info os.FileInfo) bool {
	return info.Mode()&os.ModeSymlink != 0
}
