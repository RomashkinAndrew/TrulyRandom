﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace TrulyRandom.Models;

/// <summary>
/// Base class for all modules in the graph.
/// </summary>
public abstract partial class Module : IDisposable
{
    private readonly CircularBuffer<byte> buffer = new(10 * 1024 * 1024);

    /// <summary>
    /// Main thread.
    /// </summary>
    protected Thread thread;
    /// <summary>
    /// Determines whether source should be paused when buffer is full.
    /// </summary>
    protected bool pauseOnOverflow = true;
    /// <summary>
    /// If source was paused due to overflow and amount of data in the buffer is lower then this threshold - it will be unpaused.
    /// </summary>
    protected double overflowHysteresis = 0.5;
    /// <summary>
    /// Shows whether module buffer is full.
    /// </summary>
    public bool Overflow => buffer.Count >= BufferSize;

    private string name = "";
    /// <summary>
    /// Module name.
    /// </summary>
    public string Name
    {
        get => name;
        set
        {
            if (name == value)
            {
                return;
            }

            name = value;
            if (thread != null)
            {
                thread.Name = value;
            }
        }
    }

    /// <summary>
    /// Modules with high priority will be used first by downstream modules in the graph.
    /// </summary>
    public int Priority { get; set; }

    private DataSource dataSource;
    /// <summary>
    /// A <see cref="TrulyRandom.DataSource"/>  object assigned to this module. It provides method for end-user to retrieve random data of various types.
    /// </summary>
    public DataSource DataSource
    {
        get
        {
            if (dataSource == null)
            {
                dataSource = new DataSource(this);
            }
            return dataSource;
        }
    }

    /// <summary>
    /// Object for locking access to read methods.
    /// </summary>
    public object Sync { get; } = new object();

    /// <summary>
    /// Maximum size of the output buffer.
    /// </summary>
    public int BufferSize
    {
        get => buffer.Capacity;
        set => buffer.Capacity = value;
    }

    /// <summary>
    /// Amount of bytes currently in the output buffer.
    /// </summary>
    public int BytesInBuffer => buffer.Count;

    /// <summary>
    /// Proportion of the output buffer currently occupied.
    /// </summary>
    public double BufferState => (double)BytesInBuffer / BufferSize;

    /// <summary>
    /// Total amount of bytes written to the output buffer.
    /// </summary>
    public ulong TotalBytesGenerated { get; private set; }

    /// <summary>
    /// Current generation or processing rate in bytes per second, calculated only during periods of activity.
    /// </summary>
    public int BytesPerSecond { get; protected set; }

    /// <summary>
    /// Current generation or processing rate in bytes per second, calculated during both periods of activity and inactivity.
    /// </summary>
    public int BytesPerSecondInclPause { get; private set; }

    /// <summary>
    /// Determines whether this module is started.
    /// </summary>
    public bool Started
    {
        get => ManualRun;
        set => ManualRun = value;
    }

    private bool calculateEntropy;
    /// <summary>
    /// Determines whether entropy of the last data generated should be calculated.
    /// </summary>
    public bool CalculateEntropy
    {
        get => calculateEntropy;

        set
        {
            if (calculateEntropy == value)
            {
                return;
            }
            calculateEntropy = value;
            if (!calculateEntropy)
            {
                lock (lastData)
                {
                    lastData.Clear();
                }
            }
            Entropy = 0;
        }
    }

    private bool calculateBPS = true;
    /// <summary>
    /// Determines whether BPS (bytes per second) should be calculated.
    /// </summary>
    public bool CalculateBPS
    {
        get => calculateBPS;

        set
        {
            if (calculateBPS == value)
            {
                return;
            }
            calculateBPS = value;
            if (!calculateBPS)
            {
                lock (totalBytesGeneratedHistory)
                {
                    totalBytesGeneratedHistory.Clear();
                }
            }
            BytesPerSecond = 0;
            BytesPerSecondInclPause = 0;
        }
    }

    /// <summary>
    /// Entropy of the last data generated.
    /// </summary>
    public double Entropy { get; private set; }

    private readonly System.Timers.Timer timer = new(100);
    /// <summary>
    /// Initializes a new instance of the <see cref="Module" /> class.
    /// </summary>
    protected Module()
    {
        name = GetType().Name.ToString();
        timer.AutoReset = true;
        timer.Elapsed += Timer_Elapsed;
        timer.Start();
    }

    /// <summary>
    /// Represents time interval.
    /// </summary>
    private class Interval
    {
        public DateTime Start;
        public DateTime End;

        public Interval(DateTime start, DateTime end)
        {
            Start = start;
            End = end;
        }
    }

    private readonly List<(ulong Bytes, DateTime Time)> totalBytesGeneratedHistory = new();
    private DateTime lastEntropyCalculation = DateTime.MinValue;
    private readonly List<Interval> activityIntervals = new();

    private void Timer_Elapsed(object sender, System.Timers.ElapsedEventArgs e)
    {
        if (CalculateBPS)
        {
            BPSInclPauseCalculation();
            BPSCalculation();
        }

        if (calculateEntropy && lastEntropyCalculation <= lastDataAdded && lastEntropyCalculation.WasAgo(Constants.EnrtopyCalculationPeriod))
        {
            EntropyCalculation();
        }

        PeriodicalActivity();
    }

    /// <summary>
    /// Calculates the entropy of the last data generated.
    /// </summary>
    private void EntropyCalculation()
    {
        lock (lastData)
        {
            long[] counts = new long[256];
            foreach (byte value in lastData)
            {
                counts[value]++;
            }

            double entropy = 0;
            foreach (long count in counts)
            {
                if (count != 0)
                {
                    double probability = (double)count / lastData.Count;
                    entropy -= probability * Math.Log(probability, 256);
                }
            }

            Entropy = entropy;
            lastEntropyCalculation = DateTime.Now;
        }
    }

    /// <summary>
    /// Forces immediate entropy calculation.
    /// </summary>
    public void ForceRefreshEntropy()
    {
        EntropyCalculation();
    }

    private void BPSInclPauseCalculation()
    {
        lock (totalBytesGeneratedHistory)
        {
            DateTime now = DateTime.Now;
            totalBytesGeneratedHistory.Add((TotalBytesGenerated, now));
            if (totalBytesGeneratedHistory.Count > Constants.AvgBytesPerSecondInterval.TotalMilliseconds / 100)
            {
                totalBytesGeneratedHistory.RemoveAt(0);
            }

            if (totalBytesGeneratedHistory.Count >= 10)
            {
                BytesPerSecondInclPause = (int)((TotalBytesGenerated - totalBytesGeneratedHistory[0].Bytes) / (now - totalBytesGeneratedHistory[0].Time).TotalSeconds);
            }
        }
    }

    /// <summary>
    /// Defines the default method to calculate BPS.
    /// </summary>
    protected virtual void BPSCalculation()
    {
        if (!activityIntervals.Any())
        {
            return;
        }

        lock (totalBytesGeneratedHistory)
        {
            DateTime start = activityIntervals[0].Start.Max(totalBytesGeneratedHistory[0].Time);

            ulong bytesAtStart = 0;
            foreach ((ulong Bytes, DateTime Time) in totalBytesGeneratedHistory)
            {
                if (Time >= start)
                {
                    bytesAtStart = Bytes;
                    break;
                }
            }

            TimeSpan totalOperationalTime = TimeSpan.Zero;
            foreach (Interval interval in activityIntervals)
            {
                if (interval.End <= start)
                {
                    continue;
                }
                if (interval.Start <= start)
                {
                    totalOperationalTime += interval.End.Min(DateTime.Now) - interval.Start;
                    continue;
                }
                totalOperationalTime += interval.End.Min(DateTime.Now) - start;
            }

            if (totalOperationalTime.TotalSeconds == 0)
            {
                BytesPerSecond = 0;
                return;
            }

            BytesPerSecond = (int)((TotalBytesGenerated - bytesAtStart) / totalOperationalTime.TotalSeconds);
        }
    }

    /// <summary>
    /// Provides the possibility to run some code periodically.
    /// </summary>
    protected virtual void PeriodicalActivity()
    {

    }

    /// <summary>
    /// Reads exactly <c>count</c> bytes from the output buffer. If there is insufficient data in the buffer, empty array will be returned.
    /// </summary>
    /// <param name="count">Number of bytes to be read</param>
    /// <returns>Data from the output buffer</returns>
    public byte[] ReadExactly(int count)
    {
        byte[] result;
        if (buffer.Count < count)
        {
            return Array.Empty<byte>();
        }
        lock (Sync)
        {
            if (buffer.Count < count)
            {
                return Array.Empty<byte>();
            }
            result = buffer.Read(count);
        }

        CheckForOverflow();

        return result;
    }


    /// <summary>
    /// Reads all data from the output buffer, but no less than <c>count</c> bytes. Otherwise, empty array will be returned.
    /// </summary>
    /// <param name="count">Number of bytes to be read</param>
    /// <returns>Data from the output buffer</returns>
    public byte[] ReadAtLeast(int count)
    {
        byte[] result;
        if (buffer.Count < count)
        {
            return Array.Empty<byte>();
        }
        lock (Sync)
        {
            if (buffer.Count < count)
            {
                return Array.Empty<byte>();
            }
            result = buffer.ToArray();
            buffer.Clear();
        }

        CheckForOverflow();

        return result;
    }


    /// <summary>
    /// Reads all data from the output buffer, but no more than <c>count</c> bytes.
    /// </summary>
    /// <param name="count">Number of bytes to be read</param>
    /// <returns>Data from the output buffer</returns>
    public byte[] ReadUpTo(int count)
    {
        byte[] result;
        if (!buffer.Any() || count <= 0)
        {
            return Array.Empty<byte>();
        }
        lock (Sync)
        {
            if (!buffer.Any())
            {
                return Array.Empty<byte>();
            }
            if (buffer.Count <= count)
            {
                result = buffer.ToArray();
                buffer.Clear();
            }
            else
            {
                result = buffer.Read(count);
            }
        }

        CheckForOverflow();

        return result;
    }

    /// <summary>
    /// Reads all data from the output buffer.
    /// </summary>
    /// <returns>Data from the output buffer</returns>
    public byte[] ReadAll()
    {
        byte[] result;
        if (!buffer.Any())
        {
            return Array.Empty<byte>();
        }
        lock (Sync)
        {
            result = buffer.ToArray();
            buffer.Clear();
        }

        CheckForOverflow();

        return result;
    }

    /// <summary>
    /// Clears the output buffer and discard the data.
    /// </summary>
    public void ClearBuffer()
    {
        lock (Sync)
        {
            buffer.Clear();
            CheckForOverflow();
        }
    }

    private readonly List<byte> lastData = new();
    private DateTime lastDataAdded = DateTime.MinValue;

    /// <summary>
    /// Adds data to the output buffer. In case of an overflow oldest data will be discarded.
    /// </summary>
    /// <param name="data">Data to be added</param>
    public void AddData(byte[] data)
    {
        if (data == null || data.Length == 0)
        {
            return;
        }
        TotalBytesGenerated += (uint)data.Length;
        if (!Overflow)
        {
            lock (Sync)
            {
                buffer.Write(data);
            }
            if (CalculateEntropy)
            {
                lock (lastData)
                {
                    lastData.AddRange(data);
                    if (lastData.Count > Constants.EnrtopyCalculationBytes)
                    {
                        lastData.RemoveRange(0, lastData.Count - Constants.EnrtopyCalculationBytes);
                    }
                }
            }
            lastDataAdded = DateTime.Now;
        }
        CheckForOverflow();
    }

    private bool manualRun;
    private bool overflowPause;

    /// <summary>
    /// Determines whether module is allowed to run by user.
    /// </summary>
    private bool ManualRun
    {
        get => manualRun;
        set
        {
            bool run = Run;
            manualRun = value;
            if (run != Run)
            {
                if (Run)
                {
                    StartInternal();
                }
                else
                {
                    StopInternal();
                }
            }
        }
    }

    /// <summary>
    /// Determines whether module is paused due to an overflow.
    /// </summary>
    private bool OverflowPause
    {
        get => overflowPause;
        set
        {
            bool run = Run;
            overflowPause = value;
            if (run != Run)
            {
                if (Run)
                {
                    StartInternal();
                }
                else
                {
                    StopInternal();
                }
            }
        }
    }

    /// <summary>
    /// Shows whether module should run.
    /// </summary>
    protected bool Run => !OverflowPause && ManualRun;

    /// <summary>
    /// Determines whether module should be disposed.
    /// </summary>
    protected bool shouldBeDisposed;
    /// <summary>
    /// Determines whether module is disposed.
    /// </summary>
    public bool Disposed { get; private set; }

    /// <summary>
    /// Starts the module.
    /// </summary>
    public void Start()
    {
        ManualRun = true;
    }

    /// <summary>
    /// Module start sequence.
    /// </summary>
    protected virtual void StartInternal()
    {
        lock (totalBytesGeneratedHistory)
        {
            totalBytesGeneratedHistory.Add((TotalBytesGenerated, DateTime.Now));
            activityIntervals.Add(new Interval(DateTime.Now, DateTime.MaxValue));
        }

        if (thread != null && (thread.ThreadState & ThreadState.Unstarted) == ThreadState.Unstarted)
        {
            thread.Start();
        }
    }

    /// <summary>
    /// Stops the module.
    /// </summary>
    public void Stop()
    {
        ManualRun = false;
    }

    /// <summary>
    /// Module stop sequence.
    /// </summary>
    protected virtual void StopInternal()
    {
        lock (totalBytesGeneratedHistory)
        {
            activityIntervals.Last().End = DateTime.Now;
            while (activityIntervals.Count > 1 && activityIntervals[1].Start.WasAgo(Constants.AvgBytesPerSecondInterval))
            {
                activityIntervals.RemoveAt(0);
            }
        }
    }

    /// <summary>
    /// Pauses the module in case of overflow and unpauses otherwise.
    /// </summary>
    private void CheckForOverflow()
    {
        if (pauseOnOverflow && Overflow)
        {
            OverflowPause = true;
        }

        if (OverflowPause && BufferState <= overflowHysteresis)
        {
            OverflowPause = false;
        }
    }

    /// <summary>
    /// Releases all resources used by this object.
    /// </summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Dispose the object.
    /// </summary>
    /// <param name="disposing">Indicates if disposing was initiated manually.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (Disposed)
        {
            return;
        }

        shouldBeDisposed = true;
        timer.Dispose();
        ManualRun = false;

        while (thread != null  && thread.IsAlive)
        {
            Thread.Sleep(10);
        }

        Disposed = true;
    }

    /// <summary>
    /// Finalizes the object.
    /// </summary>
    ~Module()
    {
        Dispose(false);
    }
}
