﻿using System;
using System.Collections.Generic;
using TrulyRandom.Models;

namespace TrulyRandom.Modules.Extractors;

/// <summary>
/// Treats start of the array as randomness source to randomly shuffle the remainder using Fisher–Yates algorithm. Data used as randomness source is be discarded.
/// </summary>
public class ShuffleExtractor : Extractor, ISeedable
{
    private int blockSize = 3;
    /// <summary>
    /// Block of bytes, moved together. It is recommended to use sizes as small as possible and coprime with next extractor block size (for example, hash input block size or other ShuffleExtractor block size).
    /// </summary>
    public int BlockSize
    {
        get => blockSize;
        set
        {
            if (value < 1)
            {
                throw new ArgumentException("Block size should be >= 1");
            }
            blockSize = value;
        }
    }

    /// <inheritdoc/>
    protected override bool UseDefaultCompressionCalculator => true;

    /// <inheritdoc/>
    protected override bool Seedable => true;
    /// <inheritdoc/>
    public int SeedLength { get => seedLength; set => seedLength = value; }
    /// <inheritdoc/>
    public int SeedRotationInterval { get => seedRotationInterval; set => seedRotationInterval = value; }
    /// <inheritdoc/>
    public Module SeedSource
    {
        get => seedSource;
        set
        {
            if (seedSource == value)
            {
                return;
            }
            seedSource = value;
            RotateSeed(true);
        }
    }

    /// <inheritdoc/>
    public void SetSeed(byte[] data)
    {
        if (data == null || data.Length < 1)
        {
            throw new ArgumentException($"Seed should be at least 1 byte long");
        }
        seed = data;
    }
    /// <inheritdoc/>
    public void ForceRotateSeed()
    {
        RotateSeed(true);
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ShuffleExtractor" /> class.
    /// </summary>
    public ShuffleExtractor()
    {
        BatchSize = 2_000_000;
    }

    /// <inheritdoc/>
    protected override int GetActualBatchSize()
    {
        int multiplier = 1;
        if (DynamicCoefficient > 0.2 && DynamicCoefficient < 0.5)
        {
            multiplier = 2;
        }
        else if (DynamicCoefficient > 0.5 && DynamicCoefficient <= 0.8)
        {
            multiplier = 3;
        }
        else if (DynamicCoefficient > 0.8)
        {
            multiplier = 5;
        }

        return Math.Min(BatchSize * multiplier, MaxBatchSize);
    }

    /// <inheritdoc/>
    protected override byte[] ProcessData(byte[] data)
    {
        if (data == null)
        {
            throw new ArgumentNullException(nameof(data));
        }

        int blockSize = BlockSize;
        List<int> positions = new() { 0 };

        int currentBitPointer = 0;
        int leftoverBytes;

        byte[] backupSeed = null;
        if (seed == null)
        {
            backupSeed = Utils.GetSystemRandom(SeedLength);
        }

        //Treating start of the array as randomness source, trying to get enough random numbers to perform shuffle of the remainder
        do
        {
            int? position = GenerateRandomNumberFromArrayElements(data, ref currentBitPointer, positions.Count + 1, seed ?? backupSeed);
            if (position != null)
            {
                positions.Add(position.Value);
            }

            leftoverBytes = data.Length - (currentBitPointer / 8 + (currentBitPointer % 8 == 0 ? 0 : 1));
            if (leftoverBytes < blockSize * 2)
            {
                return Array.Empty<byte>();
            }
        }
        while (leftoverBytes / blockSize > positions.Count);

        //Perform Fisher–Yates shuffle on the remainder of the input array using numbers generated above
        byte[] result = new byte[leftoverBytes / blockSize * blockSize];
        int start = data.Length - result.Length;
        int[] resultMap = new int[result.Length / blockSize];

        //To avoid moving blocks more than once we first find their final positions
        for (int i = 0; i < result.Length / blockSize; i++)
        {
            if (i != positions[i])
            {
                resultMap[i] = resultMap[positions[i]];
            }
            resultMap[positions[i]] = i;
        }

        //And only then we copy blocks to their final positions
        for (int i = 0; i < result.Length / blockSize; i++)
        {
            Array.Copy(data, i * blockSize + start, result, resultMap[i] * blockSize, blockSize);
        }

        return result;
    }

    /// <summary>
    /// Generates numbers from 0 to <paramref name="upperBound"/> using FDR (Fast Dice Roller) algorithm
    /// </summary>
    private static int? GenerateRandomNumberFromArrayElements(byte[] array, ref int currentBitPointer, int upperBound, byte[] seed)
    {
        int v = 1, c = 0;
        while (true)
        {
            v <<= 1;
            c <<= 1;

            if (currentBitPointer >= array.Length * 8)
            {
                return null;
            }

            if (GetBit(array, ref currentBitPointer, seed))
            {
                c++;
            }
            if (v >= upperBound)
            {
                if (c < upperBound)
                {
                    return c;
                }
                v -= upperBound;
                c -= upperBound;
            }
        }
    }

    /// <summary>
    /// Gets specified bit from the specified array with respect to the seed.
    /// </summary>
    private static bool GetBit(byte[] array, ref int currentBitPointer, byte[] seed)
    {
        int byteIndex = currentBitPointer / 8;
        int bitIndex = currentBitPointer % 8;

        byte currentByte = array[byteIndex];
        currentByte ^= seed[byteIndex % seed.Length];
        currentBitPointer++;
        return currentByte.Bit(bitIndex);
    }
}
