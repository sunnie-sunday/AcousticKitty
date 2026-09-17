// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 https://echo-unvaulted.net
// SPDX-FileType: SOURCE
// SPDX-FileContributor: Contributions by /xivg/

using System.Threading.Tasks;
using SQLite;

namespace AcousticKitty.Common;

internal static class DatabaseVacuum
{
	public static void RunInBackground(SQLiteConnection connection, object gate)
	{
		_ = Task.Run(() =>
		{
			lock (gate)
			{
				connection.Execute("VACUUM");
			}
		});
	}
}
