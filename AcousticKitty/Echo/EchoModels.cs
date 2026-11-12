// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 https://echo-unvaulted.net
// SPDX-FileType: SOURCE
// SPDX-FileContributor: Contributions by /xivg/

using System;
using AcousticKitty.Lodestone;

namespace AcousticKitty.Echo;

public sealed record PinnedCharacter(
	ulong? ContentId,
	ulong LodestoneId,
	string NameWorldKey,
	string Name,
	uint HomeWorldId);

public sealed record PinnedProfileSnapshot(LodestoneProfile Profile, DateTime? FetchedAtUtc);
