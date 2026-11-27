// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 https://echo-unvaulted.net
// SPDX-FileType: SOURCE
// SPDX-FileContributor: Contributions by /xivg/

using System;
using SQLite;

namespace AcousticKitty.Lodestone;

internal static class KeyedRowMigration
{
	public static void Migrate<T>(
		SQLiteConnection connection, string oldKey, string newKey, Func<T, bool> isOwnRow,
		Action<T> applyNewIdentity)
		where T : new()
	{
		var row = connection.Find<T>(oldKey);
		if (row == null || !isOwnRow(row))
		{
			return;
		}

		connection.Delete<T>(oldKey);
		if (connection.Find<T>(newKey) == null)
		{
			applyNewIdentity(row);
			connection.Insert(row);
		}
	}
}
