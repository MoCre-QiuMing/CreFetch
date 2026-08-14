using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Newtonsoft.Json;

namespace CreFetch
{
    public delegate void UrlDetectedHandler(string url);
    public delegate void TaskUpdatedHandler(string taskId, int progress, string status, string speedText);

    public class DownloadEngine : IDisposable
    {
        private readonly ConcurrentDictionary<string, TaskInfo> _tasks = new();
        private SemaphoreSlim _jobSemaphore;
        private string _savePath;
        private int _maxConcurrentJobs;
        private int _defaultThreads;
        private int _defaultChunks;
        private int _bufferSizeKb;
        private int _maxRetries;
        private readonly string _taskDbPath;

        private readonly System.Windows.Forms.Timer _clipboardTimer;
        private string _lastClipboardUrl = "";

        private System.Threading.Timer _stateSaveTimer;
        private bool _isDisposed;

        public event UrlDetectedHandler OnUrlDetected;
        public event TaskUpdatedHandler OnTaskUpdated;

        public DownloadEngine()
        {
            LoadConfig(out _savePath, out _maxConcurrentJobs, out _defaultThreads, out _defaultChunks,
                       out _bufferSizeKb, out _maxRetries);
            _jobSemaphore = new SemaphoreSlim(_maxConcurrentJobs);
            _taskDbPath = Path.Combine(_savePath, ".download_tasks", "tasks.json");
            EnsureDirectories();

            LoadTasksFromDisk();
            _stateSaveTimer = new System.Threading.Timer(_ => SaveTasksToDisk(), null, 10000, 10000);

            _clipboardTimer = new System.Windows.Forms.Timer { Interval = 500 };
            _clipboardTimer.Tick += ClipboardTimer_Tick;
        }

        public void ReloadConfig()
        {
            LoadConfig(out var newPath, out var newConcurrent, out var newThreads, out var newChunks,
                       out var newBuffer, out var newRetries);
            _savePath = newPath;
            _maxConcurrentJobs = newConcurrent;
            _defaultThreads = newThreads;
            _defaultChunks = newChunks;
            _bufferSizeKb = newBuffer;
            _maxRetries = newRetries;
            var oldSem = _jobSemaphore;
            _jobSemaphore = new SemaphoreSlim(_maxConcurrentJobs);
            oldSem.Dispose();
            EnsureDirectories();
        }

        private void LoadConfig(out string savePath, out int maxConcurrent, out int threads, out int chunks,
                                out int bufferKb, out int retries)
        {
            savePath = @"C:\Users\Administrator\Downloads";
            maxConcurrent = 1;
            threads = 8;
            chunks = 64;
            bufferKb = 1024;
            retries = 3;

            try
            {
                if (!File.Exists("config.ini")) return;
                var lines = File.ReadAllLines("config.ini");
                foreach (var line in lines)
                {
                    if (line.StartsWith("[") || string.IsNullOrWhiteSpace(line)) continue;
                    var parts = line.Split('=');
                    if (parts.Length != 2) continue;
                    var key = parts[0].Trim();
                    var val = parts[1].Trim();
                    switch (key)
                    {
                        case "save_path": savePath = val; break;
                        case "max_concurrent_jobs": int.TryParse(val, out maxConcurrent); break;
                        case "max_threads": int.TryParse(val, out threads); break;
                        case "chunk_count": int.TryParse(val, out chunks); break;
                        case "buffer_size_kb": int.TryParse(val, out bufferKb); break;
                        case "retry_times": int.TryParse(val, out retries); break;
                    }
                }
            }
            catch { }
        }

        private void EnsureDirectories()
        {
            Directory.CreateDirectory(_savePath);
            Directory.CreateDirectory(Path.Combine(_savePath, ".download_tasks"));
        }

        public string AddTask(string url)
        {
            var existing = _tasks.Values.FirstOrDefault(t => t.Url == url && t.Status != TaskStatus.COMPLETED);
            if (existing != null) return existing.TaskId;

            var info = new TaskInfo
            {
                Url = url,
                Filename = GetFilename(url),
                NumThreads = _defaultThreads,
                ChunkCount = _defaultChunks,
                Status = TaskStatus.PENDING
            };
            info.TempDir = Path.Combine(_savePath, ".download_tasks", info.TaskId);
            _tasks[info.TaskId] = info;

            Task.Run(() => SaveTasksToDisk());

            _ = TryStartTaskAsync(info.TaskId);
            return info.TaskId;
        }

        private async Task TryStartTaskAsync(string taskId)
        {
            if (!_tasks.TryGetValue(taskId, out var info)) return;
            if (info.Status == TaskStatus.COMPLETED || info.Status == TaskStatus.FAILED) return;
            if (info.RunningTask != null && !info.RunningTask.IsCompleted) return;

            await _jobSemaphore.WaitAsync();
            try
            {
                info.Status = TaskStatus.DOWNLOADING;
                info.TokenSource = new CancellationTokenSource();
                info.RunningTask = RunDownloadAsync(taskId);
                await info.RunningTask;
            }
            finally
            {
                _jobSemaphore.Release();
                var pending = _tasks.Values.Where(t => t.Status == TaskStatus.PENDING).ToList();
                foreach (var p in pending)
                    _ = TryStartTaskAsync(p.TaskId);
            }
        }

        private async Task RunDownloadAsync(string taskId)
        {
            if (!_tasks.TryGetValue(taskId, out var info)) return;

            try
            {
                var core = new DownloadCore(
                    info.Url,
                    Path.Combine(_savePath, info.Filename),
                    info.NumThreads,
                    info.ChunkCount,
                    _bufferSizeKb,
                    _maxRetries,
                    info.TokenSource.Token
                );

                core.ProgressChanged += (s, e) =>
                {
                    info.TotalSize = e.TotalBytes;
                    info.DownloadedSize = e.DownloadedBytes;
                    info.SpeedBytesPerSecond = e.Speed;
                    info.TotalTime = DateTime.Now - info.CreateTime;
                };

                core.Completed += (s, e) =>
                {
                    info.DownloadedSize = info.TotalSize;
                    info.SpeedBytesPerSecond = 0;
                    info.Status = TaskStatus.COMPLETED;
                    info.CompleteTime = DateTime.Now;
                    OnTaskUpdated?.Invoke(taskId, 100, "COMPLETED", FormatSpeed(0));
                    SaveTasksToDisk();
                    try { File.Delete(core.StateFilePath); } catch { }
                };

                core.ErrorOccurred += (s, e) =>
                {
                    info.Status = TaskStatus.FAILED;
                    info.ErrorMessage = e.Exception.Message;
                    OnTaskUpdated?.Invoke(taskId, 0, "FAILED", "");
                    SaveTasksToDisk();
                };

                await core.StartAsync();
            }
            catch (OperationCanceledException)
            {
                info.Status = TaskStatus.PAUSED;
                OnTaskUpdated?.Invoke(taskId, 0, "PAUSED", "");
                SaveTasksToDisk();
            }
            catch (Exception ex)
            {
                info.Status = TaskStatus.FAILED;
                info.ErrorMessage = ex.Message;
                OnTaskUpdated?.Invoke(taskId, 0, "FAILED", "");
                SaveTasksToDisk();
            }
        }

        private string FormatSpeed(double bytesPerSec)
        {
            if (bytesPerSec < 0) return "--";
            if (bytesPerSec < 1024) return $"{bytesPerSec:F0} B/s";
            if (bytesPerSec < 1024 * 1024) return $"{bytesPerSec / 1024:F1} KB/s";
            return $"{bytesPerSec / 1024 / 1024:F2} MB/s";
        }

        public void PauseTask(string taskId)
        {
            if (_tasks.TryGetValue(taskId, out var info))
            {
                info.TokenSource?.Cancel();
                info.Status = TaskStatus.PAUSED;
                SaveTasksToDisk();
            }
        }

        public void ResumeTask(string taskId)
        {
            if (_tasks.TryGetValue(taskId, out var info))
            {
                if (info.Status == TaskStatus.PAUSED || info.Status == TaskStatus.FAILED)
                {
                    info.TokenSource = new CancellationTokenSource();
                    info.Status = TaskStatus.PENDING;
                    SaveTasksToDisk();
                    _ = TryStartTaskAsync(taskId);
                }
            }
        }

        public void RemoveTask(string taskId, bool deleteFiles = true)
        {
            if (_tasks.TryRemove(taskId, out var info))
            {
                info.TokenSource?.Cancel();
                if (deleteFiles)
                {
                    try
                    {
                        var filePath = Path.Combine(_savePath, info.Filename);
                        if (File.Exists(filePath)) File.Delete(filePath);
                        var stateFile = filePath + ".state";
                        if (File.Exists(stateFile)) File.Delete(stateFile);
                        if (Directory.Exists(info.TempDir)) Directory.Delete(info.TempDir, true);
                    }
                    catch { }
                }
                SaveTasksToDisk();
            }
        }

        public List<TaskInfo> GetAllTasks() => _tasks.Values.ToList();

        private void LoadTasksFromDisk()
        {
            try
            {
                if (!File.Exists(_taskDbPath)) return;
                var json = File.ReadAllText(_taskDbPath);
                var list = JsonConvert.DeserializeObject<List<TaskInfo>>(json);
                if (list == null) return;
                foreach (var task in list)
                {
                    if (task.Status == TaskStatus.COMPLETED || task.Status == TaskStatus.FAILED)
                    {
                        _tasks[task.TaskId] = task;
                        continue;
                    }
                    task.Status = TaskStatus.PENDING;
                    task.TokenSource = new CancellationTokenSource();
                    _tasks[task.TaskId] = task;
                    _ = TryStartTaskAsync(task.TaskId);
                }
            }
            catch { }
        }

        private void SaveTasksToDisk()
        {
            lock (this)
            {
                try
                {
                    var list = _tasks.Values.ToList();
                    var json = JsonConvert.SerializeObject(list, Formatting.Indented);
                    var temp = _taskDbPath + ".tmp";
                    File.WriteAllText(temp, json);
                    File.Move(temp, _taskDbPath, true);
                }
                catch { }
            }
        }

        public void Start() => _clipboardTimer.Start();
        public void Stop() => _clipboardTimer.Stop();

        private void ClipboardTimer_Tick(object sender, EventArgs e)
        {
            try
            {
                if (!Clipboard.ContainsText()) return;
                string url = Clipboard.GetText();
                if ((url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                     url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) &&
                    url != _lastClipboardUrl)
                {
                    _lastClipboardUrl = url;
                    OnUrlDetected?.Invoke(url);
                }
            }
            catch { }
        }

        private string GetFilename(string url)
        {
            try
            {
                var uri = new Uri(url);
                var name = Path.GetFileName(uri.LocalPath);
                return string.IsNullOrEmpty(name) ? "download" : name;
            }
            catch { return "download"; }
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;
            _clipboardTimer?.Stop();
            _clipboardTimer?.Dispose();
            _stateSaveTimer?.Dispose();
            _jobSemaphore?.Dispose();
            SaveTasksToDisk();
            foreach (var t in _tasks.Values)
                t.TokenSource?.Cancel();
        }
    }
}