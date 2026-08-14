using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

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
        public string TaskId { get; set; } = Guid.NewGuid().ToString();
        public string Url { get; set; }
        public string Filename { get; set; }
        public long TotalSize { get; set; }
        public long DownloadedSize { get; set; }
        public TaskStatus Status { get; set; } = TaskStatus.PENDING;
        public int NumThreads { get; set; } = 8;
        public int ChunkCount { get; set; } = 64;
        public DateTime CreateTime { get; set; } = DateTime.Now;
        public DateTime? CompleteTime { get; set; }
        public string ErrorMessage { get; set; } = "";
        public double SpeedBytesPerSecond { get; set; } = 0;
        public TimeSpan TotalTime { get; set; } = TimeSpan.Zero;

        [JsonIgnore] public CancellationTokenSource TokenSource { get; set; } = new CancellationTokenSource();
        [JsonIgnore] public SemaphoreSlim SyncLock { get; } = new SemaphoreSlim(1, 1);
        [JsonIgnore] public Task RunningTask { get; set; }
        [JsonIgnore] public string TempDir { get; set; }
        [JsonIgnore] public List<long> ChunkProgress { get; set; } = new List<long>();
        [JsonIgnore] public DateTime LastSpeedTime { get; set; }
        [JsonIgnore] public long LastSpeedSize { get; set; }
    }

    public class ChunkState
    {
        public int Index { get; set; }
        public long StartOffset { get; set; }
        public long EndOffset { get; set; }
        public long Downloaded { get; set; }
        public bool IsCompleted => Downloaded >= (EndOffset - StartOffset);
    }

    public class DownloadState
    {
        public string TaskId { get; set; }
        public string Url { get; set; }
        public string FilePath { get; set; }
        public long TotalSize { get; set; }
        public int ThreadCount { get; set; }
        public int ChunkCount { get; set; }
        public List<ChunkState> Chunks { get; set; } = new List<ChunkState>();
        public DateTime LastUpdate { get; set; }
    }
}