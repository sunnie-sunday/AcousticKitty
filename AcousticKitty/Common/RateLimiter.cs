// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 https://echo-unvaulted.net
// SPDX-FileType: SOURCE
// SPDX-FileContributor: Contributions by /xivg/

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AcousticKitty.Common;

public sealed class RateLimiter(TimeSpan interval)
{
	private readonly object gate = new();
	private readonly Queue<TaskCompletionSource> highPriorityQueue = new();
	private readonly Queue<TaskCompletionSource> lowPriorityQueue = new();
	private DateTime lastCompletedUtc = DateTime.MinValue;
	private bool pumping;

	public TimeSpan Interval { get; } = interval;

	public async Task WaitAsync(bool highPriority, CancellationToken cancellationToken = default)
	{
		var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		lock (this.gate)
		{
			(highPriority ? this.highPriorityQueue : this.lowPriorityQueue).Enqueue(tcs);
			if (!this.pumping)
			{
				this.pumping = true;
				_ = this.PumpAsync();
			}
		}

		using var registration = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
		await tcs.Task.ConfigureAwait(false);
	}

	private async Task PumpAsync()
	{
		while (true)
		{
			TaskCompletionSource? next;
			lock (this.gate)
			{
				next = this.highPriorityQueue.Count > 0 ? this.highPriorityQueue.Dequeue()
					: this.lowPriorityQueue.Count > 0 ? this.lowPriorityQueue.Dequeue()
					: null;
				if (next == null)
				{
					this.pumping = false;
					return;
				}
			}

			var remaining = this.Interval - (DateTime.UtcNow - this.lastCompletedUtc);
			if (remaining > TimeSpan.Zero)
			{
				await Task.Delay(remaining).ConfigureAwait(false);
			}

			this.lastCompletedUtc = DateTime.UtcNow;
			next.TrySetResult();
		}
	}
}
