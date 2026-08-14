using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CreFetch
{
    public class DownloadCore : IDisposable
    {
        private readonly string _url;
        private readonly string _filePath;
        private readonly int _threadCount;
        private readonly int _chunkCount;
        private readonly int _bufferSize;
        private readonly int _maxRetries;
        private readonly CancellationToken _cancellationToken;

        private readonly HttpClient _httpClient;
        private ChunkState[] _chunks;
        private long _totalSize;
        private bool _isCompleted;
        private bool _disposed;

        private readonly SemaphoreSlim _chunkSemaphore;
        private readonly object _stateLock = new object();
        private string _stateFilePath;

        private double _smoothSpeed = 0;
        private const double Alpha = 0.3;
        private long _lastProgressUpdateTime = DateTime.Now.Ticks;
        private long _lastProgressDownloaded = 0;
        private const long MinUpdateIntervalTicks = TimeSpan.TicksPerSecond * 2;
        private const long MinUpdateBytes = 5 * 1024 * 1024;

        public event EventHandler<ProgressEventArgs> ProgressChanged;
        public event EventHandler Completed;
        public event EventHandler<ExceptionEventArgs> ErrorOccurred;

        public string StateFilePath => _stateFilePath;

        public DownloadCore(string url, string filePath, int threadCount, int chunkCount,
                            int bufferSizeKb, int maxRetries, CancellationToken cancellationToken)
        {
            _url = url ?? throw new ArgumentNullException(nameof(url));
            _filePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
            _threadCount = Math.Max(1, threadCount);
            _chunkCount = Math.Max(1, chunkCount);
            _bufferSize = bufferSizeKb * 1024;
            _maxRetries = Math.Max(1, maxRetries);
            _cancellationToken = cancellationToken;

            _stateFilePath = filePath + ".state";
            _chunkSemaphore = new SemaphoreSlim(_threadCount);

            var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = true,
                MaxConnectionsPerServer = _threadCount * 4,
                EnableMultipleHttp2Connections = true,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2)
            };
            _httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(10) };
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/114.0.0.0 Safari/537.36");
        }

        public async Task StartAsync()
        {
            if (_isCompleted) return;

            if (File.Exists(_stateFilePath) && File.Exists(_filePath))
            {
                if (TryLoadState())
                {
                    long totalDownloaded = _chunks.Sum(c => c.Downloaded);
                    if (totalDownloaded == _totalSize && _chunks.All(c => c.IsCompleted))
                    {
                        _isCompleted = true;
                        OnCompleted();
                        return;
                    }
                    await DownloadRemainingChunksAsync();
                    return;
                }
                try { File.Delete(_stateFilePath); } catch { }
                try { File.Delete(_filePath); } catch { }
            }

            await StartNewDownloadAsync();
        }

        private async Task StartNewDownloadAsync()
        {
            using var headReq = new HttpRequestMessage(HttpMethod.Head, _url);
            using var headResp = await _httpClient.SendAsync(headReq, HttpCompletionOption.ResponseHeadersRead, _cancellationToken);
            headResp.EnsureSuccessStatusCode();

            if (!headResp.Headers.AcceptRanges.Contains("bytes"))
                throw new NotSupportedException("服务器不支持断点续传（Range）");

            if (!headResp.Content.Headers.ContentLength.HasValue)
                throw new InvalidOperationException("无法获取文件大小");

            _totalSize = headResp.Content.Headers.ContentLength.Value;

            using (var fs = new FileStream(_filePath, FileMode.Create, FileAccess.Write, FileShare.Write, 4096, FileOptions.None))
                fs.SetLength(_totalSize);

            long chunkSize = _totalSize / _chunkCount;
            long remainder = _totalSize % _chunkCount;
            _chunks = new ChunkState[_chunkCount];
            long offset = 0;
            for (int i = 0; i < _chunkCount; i++)
            {
                long size = chunkSize + (i < remainder ? 1 : 0);
                _chunks[i] = new ChunkState
                {
                    Index = i,
                    StartOffset = offset,
                    EndOffset = offset + size,
                    Downloaded = 0
                };
                offset += size;
            }

            SaveState();
            await DownloadRemainingChunksAsync();
        }

        private async Task DownloadRemainingChunksAsync()
        {
            var tasks = new List<Task>();
            var pendingChunks = _chunks.Where(c => !c.IsCompleted).ToList();

            foreach (var chunk in pendingChunks)
            {
                await _chunkSemaphore.WaitAsync(_cancellationToken);
                var task = DownloadChunkAsync(chunk).ContinueWith(t =>
                {
                    _chunkSemaphore.Release();
                    if (t.IsFaulted && t.Exception != null)
                        OnError(t.Exception.InnerException ?? t.Exception);
                }, TaskContinuationOptions.ExecuteSynchronously);
                tasks.Add(task);
            }

            await Task.WhenAll(tasks);

            if (_chunks.All(c => c.IsCompleted))
            {
                _isCompleted = true;
                OnCompleted();
                try { File.Delete(_stateFilePath); } catch { }
            }
        }

        private async Task DownloadChunkAsync(ChunkState chunk)
        {
            int retry = 0;
            while (retry < _maxRetries)
            {
                try
                {
                    long start = chunk.StartOffset + chunk.Downloaded;
                    long end = chunk.EndOffset - 1;
                    if (start > end)
                    {
                        chunk.Downloaded = chunk.EndOffset - chunk.StartOffset;
                        return;
                    }

                    var request = new HttpRequestMessage(HttpMethod.Get, _url);
                    request.Headers.Range = new RangeHeaderValue(start, end);

                    using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, _cancellationToken);
                    response.EnsureSuccessStatusCode();

                    using var stream = await response.Content.ReadAsStreamAsync(_cancellationToken);
                    using var fileStream = new FileStream(_filePath, FileMode.Open, FileAccess.Write, FileShare.Write,
                                                          _bufferSize, FileOptions.Asynchronous);
                    fileStream.Seek(chunk.StartOffset + chunk.Downloaded, SeekOrigin.Begin);

                    byte[] buffer = ArrayPool<byte>.Shared.Rent(_bufferSize);
                    try
                    {
                        int bytesRead;
                        while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, _cancellationToken)) > 0)
                        {
                            await fileStream.WriteAsync(buffer, 0, bytesRead, _cancellationToken);
                            chunk.Downloaded += bytesRead;

                            long totalDownloaded = _chunks.Sum(c => c.Downloaded);
                            long now = DateTime.Now.Ticks;
                            long elapsed = now - _lastProgressUpdateTime;
                            long downloadedSince = totalDownloaded - _lastProgressDownloaded;

                            if (elapsed >= MinUpdateIntervalTicks || downloadedSince >= MinUpdateBytes)
                            {
                                double instantSpeed = downloadedSince * (double)TimeSpan.TicksPerSecond / elapsed;
                                if (_smoothSpeed == 0)
                                    _smoothSpeed = instantSpeed;
                                else
                                    _smoothSpeed = Alpha * instantSpeed + (1 - Alpha) * _smoothSpeed;
                                if (_smoothSpeed < 0) _smoothSpeed = 0;

                                _lastProgressUpdateTime = now;
                                _lastProgressDownloaded = totalDownloaded;

                                OnProgressChanged(totalDownloaded, _totalSize, _smoothSpeed);
                                SaveState();
                            }
                        }
                        return;
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(buffer);
                    }
                }
                catch (OperationCanceledException) when (_cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception) when (retry < _maxRetries - 1)
                {
                    retry++;
                    await Task.Delay(1000 * (int)Math.Pow(2, retry), _cancellationToken);
                }
                catch (Exception ex)
                {
                    throw new Exception($"块 {chunk.StartOffset}-{chunk.EndOffset} 下载失败", ex);
                }
            }
        }

        private bool TryLoadState()
        {
            try
            {
                var json = File.ReadAllText(_stateFilePath);
                var state = JsonSerializer.Deserialize<DownloadState>(json);
                if (state == null || state.Chunks.Count != _chunkCount) return false;
                _totalSize = state.TotalSize;
                _chunks = state.Chunks.Select(c => new ChunkState
                {
                    Index = c.Index,
                    StartOffset = c.StartOffset,
                    EndOffset = c.EndOffset,
                    Downloaded = c.Downloaded
                }).ToArray();
                return true;
            }
            catch { return false; }
        }

        private void SaveState()
        {
            lock (_stateLock)
            {
                try
                {
                    var state = new DownloadState
                    {
                        TaskId = Guid.NewGuid().ToString(),
                        Url = _url,
                        FilePath = _filePath,
                        TotalSize = _totalSize,
                        ThreadCount = _threadCount,
                        ChunkCount = _chunkCount,
                        Chunks = _chunks.Select(c => new ChunkState
                        {
                            Index = c.Index,
                            StartOffset = c.StartOffset,
                            EndOffset = c.EndOffset,
                            Downloaded = c.Downloaded
                        }).ToList(),
                        LastUpdate = DateTime.Now
                    };
                    var json = JsonSerializer.Serialize(state);
                    var temp = _stateFilePath + ".tmp";
                    File.WriteAllText(temp, json);
                    File.Move(temp, _stateFilePath, true);
                }
                catch { }
            }
        }

        private void OnProgressChanged(long downloaded, long total, double speed)
            => ProgressChanged?.Invoke(this, new ProgressEventArgs(downloaded, total, speed));

        private void OnCompleted() => Completed?.Invoke(this, EventArgs.Empty);
        private void OnError(Exception ex) => ErrorOccurred?.Invoke(this, new ExceptionEventArgs(ex));

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _httpClient?.Dispose();
            _chunkSemaphore?.Dispose();
        }
    }

    public class ProgressEventArgs : EventArgs
    {
        public long DownloadedBytes { get; }
        public long TotalBytes { get; }
        public double Speed { get; }
        public double Percentage => TotalBytes > 0 ? (double)DownloadedBytes / TotalBytes * 100 : 0;
        public ProgressEventArgs(long downloaded, long total, double speed)
        {
            DownloadedBytes = downloaded;
            TotalBytes = total;
            Speed = speed;
        }
    }

    public class ExceptionEventArgs : EventArgs
    {
        public Exception Exception { get; }
        public ExceptionEventArgs(Exception ex) => Exception = ex;
    }
}