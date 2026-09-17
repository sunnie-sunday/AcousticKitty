// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 https://echo-unvaulted.net
// SPDX-FileType: SOURCE
// SPDX-FileContributor: Contributions by /xivg/

using System;
using System.Collections.Generic;
using System.Linq;

namespace AcousticKitty.Common;

internal static class OldestFirstEviction
{
	public static void Trim<T>(
		List<T> entries, int cap, Func<T, bool> isEvictable, Func<T, DateTime> timestampSelector,
		Action<IReadOnlyList<T>> deleteMany)
	{
		var evictable = entries.Where(isEvictable).ToList();
		var excess = evictable.Count - cap;
		if (excess <= 0)
		{
			return;
		}

		var oldestHeap =
			new PriorityQueue<T, DateTime>(Comparer<DateTime>.Create((a, b) => b.CompareTo(a)));
		foreach (var item in evictable)
		{
			var timestamp = timestampSelector(item);
			if (oldestHeap.Count < excess)
			{
				oldestHeap.Enqueue(item, timestamp);
			}
			else if (oldestHeap.TryPeek(out _, out var newestOfTheOldest) &&
				timestamp < newestOfTheOldest)
			{
				oldestHeap.Dequeue();
				oldestHeap.Enqueue(item, timestamp);
			}
		}

		var oldest = new List<T>(oldestHeap.Count);
		while (oldestHeap.TryDequeue(out var item, out _))
		{
			oldest.Add(item);
		}

		deleteMany(oldest);
		var oldestSet = new HashSet<T>(oldest);
		entries.RemoveAll(oldestSet.Contains);
	}
}
