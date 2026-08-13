using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Newtonsoft.Json;

namespace CreFetch
{
    public enum TaskStatus
    {
        PENDING,
        DOWNLOADING,
        PAUSED,
        COMPLETED,
        FAILED
    }

    public class TaskInfo
    {
        public string TaskId { get; set; }
        public string Url { get; set; }
        public string Filename { get; set; }
        public long TotalSize { get; set; }
        public long DownloadedSize { get; set; }
        public List<long> ChunkProgress { get; set; }
        public string TempDir { get; set; }
        public TaskStatus Status { get; set; }
        public int NumThreads { get; set; }
        public DateTime CreateTime { get; set; }
        public string ErrorMessage { get; set; } = "";

        public double SpeedBytesPerSecond { get; set; } = 0;
        public DateTime LastSpeedTime { get; set; }
        public long LastSpeedSize { get; set; }
        public TimeSpan TotalTime { get; set; } = TimeSpan.Zero;
        public CancellationTokenSource TokenSource { get; set; }
    }

    public delegate void UrlDetectedHandler(string url);
    public delegate void TaskUpdatedHandler(string taskId, int progress, string status);

    public class DownloadEngine
    {
        private readonly Dictionary<string, TaskInfo> tasks = new();
        private readonly object dictLock = new();
        private string savePath = @"C:\Users\Administrator\Downloads";
        private int maxThreads = 50;
        private readonly HttpClient client;
        private readonly string stateFile;
        private readonly System.Windows.Forms.Timer clipboardTimer;
        private string lastUrl = "";

        public event UrlDetectedHandler OnUrlDetected;
        public event TaskUpdatedHandler OnTaskUpdated;

        public DownloadEngine()
        {
            var handler = new HttpClientHandler();
            handler.ServerCertificateCustomValidationCallback = (sender, cert, chain, sslPolicyErrors) => true;
            handler.AllowAutoRedirect = true;
            handler.AutomaticDecompression = System.Net.DecompressionMethods.None;

            client = new HttpClient(handler);
            client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
            client.Timeout = TimeSpan.FromSeconds(60);

            LoadConfig();
            EnsureSavePath();
            stateFile = Path.Combine(savePath, ".download_tasks", "resume.json");
            LoadTasksFromDisk();

            clipboardTimer = new System.Windows.Forms.Timer();
            clipboardTimer.Interval = 500;
            clipboardTimer.Tick += ClipboardTimer_Tick;
        }

        public void Start() => clipboardTimer.Start();
        public void Stop() { clipboardTimer.Stop(); SaveTasksToDisk(); }

        private void ClipboardTimer_Tick(object sender, EventArgs e)
        {
            try
            {
                if (Clipboard.ContainsText())
                {
                    string url = Clipboard.GetText();
                    if (IsValidUrl(url) && url != lastUrl)
                    {
                        lastUrl = url;
                        OnUrlDetected?.Invoke(url);
                    }
                }
            }
            catch { }
        }

        private void LoadConfig()
        {
            try
            {
                if (File.Exists("config.ini"))
                {
                    var lines = File.ReadAllLines("config.ini");
                    foreach (var line in lines)
                    {
                        var parts = line.Split('=');
                        if (parts.Length == 2)
                        {
                            var key = parts[0].Trim();
                            var value = parts[1].Trim();
                            if (key == "max_threads") int.TryParse(value, out maxThreads);
                            else if (key == "save_path") savePath = value;
                        }
                    }
                }
            }
            catch { }
        }

        private void EnsureSavePath()
        {
            Directory.CreateDirectory(savePath);
            Directory.CreateDirectory(Path.Combine(savePath, ".download_tasks"));
        }

        private bool IsValidUrl(string text) =>
            text.StartsWith("http://") || text.StartsWith("https://");

        public string AddTask(string url)
        {
            lock (dictLock)
            {
                foreach (var pair in tasks)
                    if (pair.Value.Url == url && pair.Value.Status != TaskStatus.COMPLETED)
                        return pair.Key;

                string taskId = Guid.NewGuid().ToString();
                var info = new TaskInfo
                {
                    TaskId = taskId,
                    Url = url,
                    Filename = GetFilename(url),
                    TotalSize = 0,
                    Status = TaskStatus.PENDING,
                    NumThreads = maxThreads,
                    TempDir = Path.Combine(savePath, ".download_tasks", taskId),
                    CreateTime = DateTime.Now,
                    ChunkProgress = new List<long>(),
                    ErrorMessage = ""
                };
                for (int i = 0; i < info.NumThreads; i++) info.ChunkProgress.Add(0);
                Directory.CreateDirectory(info.TempDir);
                tasks[info.TaskId] = info;
                SaveTasksToDisk();

                Task.Run(() => DownloadTask(taskId));
                return taskId;
            }
        }

        public void PauseTask(string taskId)
        {
            lock (dictLock)
            {
                if (tasks.TryGetValue(taskId, out var info) && info.Status == TaskStatus.DOWNLOADING)
                {
                    info.TokenSource?.Cancel();
                    info.Status = TaskStatus.PAUSED;
                    SaveTasksToDisk();
                }
            }
        }

        public void ResumeTask(string taskId)
        {
            lock (dictLock)
            {
                if (tasks.TryGetValue(taskId, out var info) &&
                    (info.Status == TaskStatus.PAUSED || info.Status == TaskStatus.PENDING))
                {
                    info.Status = TaskStatus.DOWNLOADING;
                    info.TokenSource = new CancellationTokenSource();
                    SaveTasksToDisk();
                    Task.Run(() => DownloadTask(taskId));
                }
            }
        }

        public void RemoveTask(string taskId)
        {
            lock (dictLock)
            {
                if (tasks.TryGetValue(taskId, out var info))
                {
                    info.TokenSource?.Cancel();
                    try { Directory.Delete(info.TempDir, true); } catch { }
                    tasks.Remove(taskId);
                    SaveTasksToDisk();
                }
            }
        }

        public int GetTaskProgress(string taskId)
        {
            lock (dictLock)
            {
                if (!tasks.TryGetValue(taskId, out var info)) return -1;
                if (info.TotalSize == 0) return 0;
                return (int)((info.DownloadedSize * 100) / info.TotalSize);
            }
        }

        public List<TaskInfo> GetAllTasks()
        {
            List<TaskInfo> snapshot;
            lock (dictLock) { snapshot = tasks.Values.ToList(); }

            var now = DateTime.Now;
            foreach (var task in snapshot)
            {
                lock (task)
                {
                    if (task.Status == TaskStatus.DOWNLOADING)
                    {
                        if (task.LastSpeedTime != DateTime.MinValue)
                        {
                            double seconds = (now - task.LastSpeedTime).TotalSeconds;
                            if (seconds >= 0.5)
                            {
                                task.SpeedBytesPerSecond = (task.DownloadedSize - task.LastSpeedSize) / seconds;
                                task.LastSpeedSize = task.DownloadedSize;
                                task.LastSpeedTime = now;
                            }
                        }
                        else
                        {
                            task.LastSpeedSize = task.DownloadedSize;
                            task.LastSpeedTime = now;
                        }
                    }
                    else if (task.Status == TaskStatus.PAUSED)
                    {
                        task.SpeedBytesPerSecond = 0;
                    }

                    if (task.Status == TaskStatus.COMPLETED)
                    {
                        if (task.TotalTime == TimeSpan.Zero) task.TotalTime = now - task.CreateTime;
                    }
                    else
                    {
                        task.TotalTime = now - task.CreateTime;
                    }
                }
            }
            return snapshot;
        }

        private async Task<long> GetFileSize(string url)
        {
            try
            {
                var request = new HttpRequestMessage(HttpMethod.Head, url);
                request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
                var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                if (response.IsSuccessStatusCode && response.Content.Headers.ContentLength.HasValue)
                {
                    return response.Content.Headers.ContentLength.Value;
                }
            }
            catch { }
            return 0;
        }

        private string GetFilename(string url)
        {
            var uri = new Uri(url);
            var name = Path.GetFileName(uri.LocalPath);
            return string.IsNullOrEmpty(name) ? "download" : name;
        }

        private async Task DownloadTask(string taskId)
        {
            TaskInfo info;
            lock (dictLock) { if (!tasks.TryGetValue(taskId, out info)) return; }

            if (info.TokenSource == null || info.TokenSource.IsCancellationRequested)
            {
                info.TokenSource = new CancellationTokenSource();
            }

            if (info.TotalSize == 0)
            {
                long size = await GetFileSize(info.Url);
                if (size <= 0)
                {
                    Console.WriteLine($"[降级] 无法获取文件大小，使用完整下载模式");
                    await DownloadFullFile(info);
                    return;
                }
                info.TotalSize = size;
            }

            if (info.DownloadedSize >= info.TotalSize)
            {
                info.Status = TaskStatus.COMPLETED;
                try { MergeFiles(info); }
                catch (Exception ex)
                {
                    info.Status = TaskStatus.FAILED;
                    info.ErrorMessage = $"合并文件失败: {ex.Message}";
                    OnTaskUpdated?.Invoke(taskId, 0, "FAILED");
                    SaveTasksToDisk();
                    return;
                }
                OnTaskUpdated?.Invoke(taskId, 100, "COMPLETED");
                SaveTasksToDisk();
                return;
            }

            int threads = info.NumThreads;
            long chunkSize = info.TotalSize / threads;
            var tasksList = new List<Task>();
            info.Status = TaskStatus.DOWNLOADING;
            SaveTasksToDisk();

            for (int i = 0; i < threads; i++)
            {
                int idx = i;
                long start = idx * chunkSize + info.ChunkProgress[idx];
                long end = (idx == threads - 1) ? info.TotalSize - 1 : (idx + 1) * chunkSize - 1;
                if (start <= end)
                    tasksList.Add(DownloadChunk(info.Url, start, end, idx, info));
            }

            try { await Task.WhenAll(tasksList); }
            catch (OperationCanceledException) { Console.WriteLine($"[{taskId}] 已暂停。"); }
            catch { }

            long total = 0;
            for (int i = 0; i < threads; i++) total += info.ChunkProgress[i];
            info.DownloadedSize = total;

            if (info.DownloadedSize >= info.TotalSize)
            {
                info.Status = TaskStatus.COMPLETED;
                try { MergeFiles(info); }
                catch (Exception ex)
                {
                    info.Status = TaskStatus.FAILED;
                    info.ErrorMessage = $"合并文件失败: {ex.Message}";
                    OnTaskUpdated?.Invoke(taskId, 0, "FAILED");
                    SaveTasksToDisk();
                    return;
                }
                OnTaskUpdated?.Invoke(taskId, 100, "COMPLETED");
            }
            else if (info.Status == TaskStatus.PAUSED)
            {
                OnTaskUpdated?.Invoke(taskId, GetTaskProgress(taskId), "PAUSED");
            }
            else
            {
                Console.WriteLine($"[降级] 分块卡死，切换为完整下载模式补全尾部");
                await DownloadFullFile(info);
            }
            SaveTasksToDisk();
        }

        private async Task DownloadFullFile(TaskInfo info)
        {
            try
            {
                var request = new HttpRequestMessage(HttpMethod.Get, info.Url);
                request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
                var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

                if (!response.IsSuccessStatusCode)
                {
                    info.Status = TaskStatus.FAILED;
                    info.ErrorMessage = $"HTTP错误: {response.StatusCode}";
                    OnTaskUpdated?.Invoke(info.TaskId, 0, "FAILED");
                    SaveTasksToDisk();
                    return;
                }

                var contentType = response.Content.Headers.ContentType?.MediaType;
                if (!string.IsNullOrEmpty(contentType) && contentType.StartsWith("text/html", StringComparison.OrdinalIgnoreCase))
                {
                    info.Status = TaskStatus.FAILED;
                    info.ErrorMessage = "服务器返回了 HTML 页面，可能不是有效下载链接";
                    OnTaskUpdated?.Invoke(info.TaskId, 0, "FAILED");
                    SaveTasksToDisk();
                    return;
                }

                var totalSize = response.Content.Headers.ContentLength ?? -1;
                if (totalSize == 0)
                {
                    info.Status = TaskStatus.FAILED;
                    info.ErrorMessage = "服务器返回的文件大小为0字节";
                    OnTaskUpdated?.Invoke(info.TaskId, 0, "FAILED");
                    SaveTasksToDisk();
                    return;
                }

                info.TotalSize = totalSize > 0 ? totalSize : 0;
                string finalPath = Path.Combine(savePath, info.Filename);
                using var fs = new FileStream(finalPath, FileMode.Append, FileAccess.Write);
                var stream = await response.Content.ReadAsStreamAsync();
                byte[] buffer = new byte[131072];
                int bytesRead;
                long downloaded = fs.Length;
                int lastReportedProgress = -1;
                long lastSpeedUpdate = downloaded;
                long lastSpeedSize = downloaded;
                DateTime lastSpeedTime = DateTime.Now;

                if (downloaded > 0)
                {
                    Console.WriteLine($"[单线程] 发现已有 {downloaded} 字节，从断点继续下载");
                }

                info.Status = TaskStatus.DOWNLOADING;
                SaveTasksToDisk();

                while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, info.TokenSource.Token)) > 0)
                {
                    await fs.WriteAsync(buffer, 0, bytesRead);
                    downloaded += bytesRead;

                    if (downloaded - lastSpeedUpdate >= 1024 * 1024)
                    {
                        lastSpeedUpdate = downloaded;
                        lock (info)
                        {
                            info.LastSpeedSize = downloaded;
                            info.LastSpeedTime = DateTime.Now;
                        }
                    }

                    if (totalSize > 0)
                    {
                        int progress = (int)((downloaded * 100) / totalSize);
                        if (progress != lastReportedProgress)
                        {
                            lastReportedProgress = progress;
                            OnTaskUpdated?.Invoke(info.TaskId, progress, "DOWNLOADING");
                        }
                    }
                }

                info.SpeedBytesPerSecond = 0;
                info.Status = TaskStatus.COMPLETED;
                info.DownloadedSize = downloaded;
                OnTaskUpdated?.Invoke(info.TaskId, 100, "COMPLETED");
                SaveTasksToDisk();
            }
            catch (OperationCanceledException)
            {
                info.Status = TaskStatus.PAUSED;
                SaveTasksToDisk();
                return;
            }
            catch (Exception ex)
            {
                info.Status = TaskStatus.FAILED;
                info.ErrorMessage = ex.Message;
                OnTaskUpdated?.Invoke(info.TaskId, 0, "FAILED");
                SaveTasksToDisk();
            }
        }

        private async Task DownloadChunk(string url, long start, long end, int index, TaskInfo info)
        {
            string chunkPath = Path.Combine(info.TempDir, $"chunk_{index}.tmp");
            var fileMode = File.Exists(chunkPath) ? FileMode.Append : FileMode.Create;
            using var fs = new FileStream(chunkPath, fileMode, FileAccess.Write, FileShare.Read, 131072, FileOptions.Asynchronous | FileOptions.SequentialScan);
            fs.Position = fs.Length;

            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(start + fs.Position, end);
            request.Headers.UserAgent.ParseAdd("Mozilla/5.0");

            byte[] buffer = new byte[131072];

            try
            {
                using (var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead))
                {
                    if (!response.IsSuccessStatusCode) return;
                    var stream = await response.Content.ReadAsStreamAsync();
                    int bytesRead;
                    long bytesSinceUpdate = 0;
                    const long UpdateThreshold = 1024 * 1024;

                    while (true)
                    {
                        var readTask = stream.ReadAsync(buffer, 0, buffer.Length, info.TokenSource.Token);
                        var timeoutTask = Task.Delay(15000, info.TokenSource.Token);
                        var completedTask = await Task.WhenAny(readTask, timeoutTask);

                        if (completedTask == timeoutTask)
                        {
                            throw new TimeoutException("分块网络超时，准备降级");
                        }
                        bytesRead = await readTask;
                        if (bytesRead == 0) break;

                        await fs.WriteAsync(buffer, 0, bytesRead);
                        bytesSinceUpdate += bytesRead;

                        if (bytesSinceUpdate >= UpdateThreshold)
                        {
                            lock (info.ChunkProgress) { info.ChunkProgress[index] = fs.Position; }
                            bytesSinceUpdate = 0;
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine($"[分块 {index}] 已响应暂停");
                throw;
            }
            catch (TimeoutException)
            {
                Console.WriteLine($"[分块 {index}] 超时，触发降级");
                throw;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[分块 {index}] 异常: {ex.Message}");
                throw;
            }
        }

        private void MergeFiles(TaskInfo info)
        {
            string finalPath = Path.Combine(savePath, info.Filename);
            string tempMergePath = finalPath + ".merging";

            bool merged = false;
            for (int retry = 0; retry < 3; retry++)
            {
                try
                {
                    using (var outStream = new FileStream(tempMergePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite, 81920, FileOptions.SequentialScan))
                    {
                        for (int i = 0; i < info.NumThreads; i++)
                        {
                            string chunkPath = Path.Combine(info.TempDir, $"chunk_{i}.tmp");
                            if (File.Exists(chunkPath))
                            {
                                using var inStream = File.OpenRead(chunkPath);
                                inStream.CopyTo(outStream);
                            }
                        }
                    }
                    merged = true;
                    break;
                }
                catch (IOException)
                {
                    if (retry == 2) throw;
                    Thread.Sleep(200);
                }
            }

            if (merged)
            {
                if (File.Exists(finalPath)) File.Delete(finalPath);
                File.Move(tempMergePath, finalPath);

                for (int i = 0; i < info.NumThreads; i++)
                {
                    string chunkPath = Path.Combine(info.TempDir, $"chunk_{i}.tmp");
                    if (File.Exists(chunkPath))
                    {
                        for (int retry = 0; retry < 3; retry++)
                        {
                            try { File.Delete(chunkPath); break; }
                            catch (IOException) { Thread.Sleep(100); }
                        }
                    }
                }
                try { Directory.Delete(info.TempDir, true); } catch { }
            }
        }

        private void SaveTasksToDisk()
        {
            List<TaskInfo> snapshot;
            lock (dictLock) { snapshot = tasks.Values.Where(t => t.Status != TaskStatus.COMPLETED).ToList(); }
            try
            {
                var json = JsonConvert.SerializeObject(snapshot, Newtonsoft.Json.Formatting.Indented);
                File.WriteAllText(stateFile, json);
            }
            catch { }
        }

        private void LoadTasksFromDisk()
        {
            if (!File.Exists(stateFile)) return;
            try
            {
                var json = File.ReadAllText(stateFile);
                var list = JsonConvert.DeserializeObject<List<TaskInfo>>(json);
                if (list != null)
                {
                    lock (dictLock)
                    {
                        foreach (var info in list)
                        {
                            info.ChunkProgress = new List<long>();
                            for (int i = 0; i < info.NumThreads; i++)
                            {
                                string chunkPath = Path.Combine(info.TempDir, $"chunk_{i}.tmp");
                                info.ChunkProgress.Add(File.Exists(chunkPath) ? new FileInfo(chunkPath).Length : 0);
                            }
                            tasks[info.TaskId] = info;
                        }
                    }
                    foreach (var info in list)
                    {
                        if (info.Status == TaskStatus.PAUSED || info.Status == TaskStatus.PENDING)
                            ResumeTask(info.TaskId);
                    }
                }
            }
            catch { }
        }
    }
}