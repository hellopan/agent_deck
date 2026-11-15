package codexprobe

import (
	"os"
	"testing"
)

func TestCheckInstalledCodex(t *testing.T) {
	executable := os.Getenv("AGENTDECK_TEST_CODEX")
	if executable == "" {
		t.Skip("set AGENTDECK_TEST_CODEX to an installed Codex CLI")
	}
	t.Setenv("OPENAI_API_KEY", "")
	t.Setenv("CODEX_API_KEY", "")
	status, err := Check(t.Context(), Config{
		Executable: executable,
		ProjectDir: t.TempDir(),
		CodexHome:  t.TempDir(),
	})
	if err != nil {
		t.Fatal(err)
	}
	if status.AccountConfigured {
		t.Fatal("fresh Codex home unexpectedly returned a configured account")
	}
	t.Logf("Codex stdio initialized with a fresh home, OpenAI auth required: %t", status.OpenAIAuthRequired)
}
