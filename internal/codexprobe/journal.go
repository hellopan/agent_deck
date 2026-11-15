package codexprobe

import (
	"bufio"
	"crypto/rand"
	"encoding/hex"
	"encoding/json"
	"errors"
	"io"
	"os"
	"path/filepath"
	"strings"
)

const maxRecord = 2 * 1024 * 1024
const maxJournal = 8 * 1024 * 1024

// Journal is written by the one process that owns a task.
type Journal struct {
	file *os.File
	size int
	ID   string
}

type journalScope struct {
	ProjectID string `json:"projectId"`
	Directory string `json:"directory"`
}

type TaskPage struct {
	Events []TaskEvent `json:"events"`
	Next   int         `json:"next"`
}

func NewJournal(root, projectID, directory string) (*Journal, error) {
	id := make([]byte, 16)
	if _, err := rand.Read(id); err != nil {
		return nil, err
	}
	return OpenNewJournal(root, projectID, directory, hex.EncodeToString(id))
}

// OpenNewJournal starts an exclusive log for a task with a previously issued ID.
func OpenNewJournal(root, projectID, directory, taskID string) (*Journal, error) {
	if !filepath.IsAbs(root) || !filepath.IsAbs(directory) {
		return nil, errors.New("task log and project directories must be absolute")
	}
	if _, err := hex.DecodeString(taskID); err != nil || len(taskID) != 32 || strings.ToLower(taskID) != taskID {
		return nil, errors.New("invalid task ID")
	}
	file, err := os.OpenFile(filepath.Join(root, taskID+".log"), os.O_WRONLY|os.O_CREATE|os.O_EXCL, 0o600)
	if err != nil {
		return nil, err
	}
	journal := &Journal{file: file, ID: taskID}
	if err := journal.append(journalScope{ProjectID: projectID, Directory: directory}, maxJournal); err != nil {
		file.Close()
		return nil, err
	}
	return journal, nil
}

func (j *Journal) Append(event TaskEvent) error {
	// Leave room for a final diagnostic even when the task reaches its limit.
	return j.append(event, maxJournal-4096)
}

func (j *Journal) Finish(status string) error {
	if !IsFinalTaskStatus(status) {
		return errors.New("invalid final task status")
	}
	return j.append(TaskEvent{Type: "task/finished", Status: status}, maxJournal)
}

func IsFinalTaskStatus(status string) bool {
	return status == "completed" || status == "failed" || status == "interrupted"
}

func (j *Journal) Close() error { return j.file.Close() }

// RequestCancellation records a local request for an existing journal.
func RequestCancellation(root, id string) error {
	return requestCancellation(root, id, nil)
}

// RequestProjectCancellation checks the journal's scope before recording a
// cancellation request on behalf of a paired client.
func RequestProjectCancellation(root, id, projectID, directory string) error {
	return requestCancellation(root, id, &journalScope{ProjectID: projectID, Directory: directory})
}

func requestCancellation(root, id string, scope *journalScope) error {
	if _, err := hex.DecodeString(id); !filepath.IsAbs(root) || err != nil || len(id) != 32 || strings.ToLower(id) != id {
		return errors.New("invalid task log directory or task ID")
	}
	files, err := os.OpenRoot(root)
	if err != nil {
		return err
	}
	defer files.Close()
	info, err := files.Lstat(id + ".log")
	if err != nil || !info.Mode().IsRegular() {
		return errors.New("task log unavailable")
	}
	if scope != nil {
		file, err := files.Open(id + ".log")
		if err != nil {
			return err
		}
		defer file.Close()
		if err := checkJournalScope(bufio.NewReaderSize(io.LimitReader(file, 16*1024), 16*1024), *scope); err != nil {
			return err
		}
	}
	marker, err := files.OpenFile(id+".cancel.log", os.O_CREATE|os.O_EXCL|os.O_WRONLY, 0o600)
	if errors.Is(err, os.ErrExist) {
		return nil
	}
	if err != nil {
		return err
	}
	return marker.Close()
}

func checkJournalScope(reader *bufio.Reader, expected journalScope) error {
	header, err := reader.ReadSlice('\n')
	var scope journalScope
	if err != nil || json.Unmarshal(header, &scope) != nil || scope != expected {
		return errors.New("task log unavailable for this project")
	}
	return nil
}

func (j *Journal) CancellationRequested() (bool, error) {
	_, err := os.Lstat(filepath.Join(filepath.Dir(j.file.Name()), j.ID+".cancel.log"))
	if errors.Is(err, os.ErrNotExist) {
		return false, nil
	}
	return err == nil, err
}

func (j *Journal) append(record any, limit int) error {
	line, err := json.Marshal(record)
	if err != nil {
		return err
	}
	line = append(line, '\n')
	if len(line) > maxRecord || j.size+len(line) > limit {
		return errors.New("task log size limit reached")
	}
	n, err := j.file.Write(line)
	j.size += n
	if err != nil {
		return err
	}
	return j.file.Sync()
}

// ReadJournal returns a bounded page after a zero-based event cursor.
// The caller supplies the currently authorized directory, not a client path.
func ReadJournal(root, id, projectID, directory string, after int) (TaskPage, error) {
	if after < 0 {
		return TaskPage{}, errors.New("invalid task log cursor")
	}
	var page TaskPage
	err := withJournal(root, id, journalScope{ProjectID: projectID, Directory: directory}, func(reader *bufio.Reader) error {
		var err error
		page, err = readJournalPage(reader, after)
		return err
	})
	return page, err
}

// FinalJournalStatus finds the last committed terminal event. An empty result
// means there is no terminal event, which does not establish process liveness.
func FinalJournalStatus(root, id, projectID, directory string) (string, error) {
	var status string
	err := withJournal(root, id, journalScope{ProjectID: projectID, Directory: directory}, func(reader *bufio.Reader) error {
		for {
			line, err := reader.ReadSlice('\n')
			if errors.Is(err, io.EOF) {
				return nil
			}
			if err != nil {
				return err
			}
			var event TaskEvent
			if err := json.Unmarshal(line, &event); err != nil {
				return errors.New("invalid task log event")
			}
			if event.Type == "task/finished" {
				if !IsFinalTaskStatus(event.Status) {
					return errors.New("invalid final task status")
				}
				status = event.Status
			}
		}
	})
	return status, err
}

func withJournal(root, id string, scope journalScope, read func(*bufio.Reader) error) error {
	if !filepath.IsAbs(root) || len(id) != 32 || strings.ToLower(id) != id {
		return errors.New("invalid task log query")
	}
	if _, err := hex.DecodeString(id); err != nil {
		return errors.New("invalid task ID")
	}
	files, err := os.OpenRoot(root)
	if err != nil {
		return err
	}
	defer files.Close()
	file, err := files.Open(id + ".log")
	if err != nil {
		return err
	}
	defer file.Close()
	info, err := file.Stat()
	if err != nil || !info.Mode().IsRegular() || info.Size() > maxJournal {
		return errors.New("invalid task log")
	}
	reader := bufio.NewReaderSize(io.LimitReader(file, info.Size()), maxRecord)
	if err := checkJournalScope(reader, scope); err != nil {
		return err
	}
	return read(reader)
}

func readJournalPage(reader *bufio.Reader, after int) (TaskPage, error) {
	page := TaskPage{Events: make([]TaskEvent, 0), Next: after}
	pageBytes := 0
	for index := 0; ; index++ {
		line, err := reader.ReadSlice('\n')
		if errors.Is(err, io.EOF) {
			if index < after {
				return TaskPage{}, errors.New("task log cursor exceeds the available events")
			}
			return page, nil // An unfinished last record is never returned.
		}
		if err != nil {
			return TaskPage{}, err
		}
		if index < after {
			continue
		}
		if len(page.Events) == 64 || (len(page.Events) > 0 && pageBytes+len(line) > maxRecord) {
			return page, nil
		}
		var event TaskEvent
		if err := json.Unmarshal(line, &event); err != nil {
			return TaskPage{}, errors.New("invalid task log event")
		}
		page.Events = append(page.Events, event)
		page.Next++
		pageBytes += len(line)
	}
}
