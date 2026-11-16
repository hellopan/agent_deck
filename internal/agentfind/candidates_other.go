//go:build !windows

package agentfind

// candidates has no non-Windows implementation: this project drives desktop
// applications that only exist on Windows, so detection reports "not found"
// rather than inventing a search.
func candidates(name string) []candidate { return nil }
