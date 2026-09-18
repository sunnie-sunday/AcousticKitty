// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 https://echo-unvaulted.net
// SPDX-FileType: SOURCE
// SPDX-FileContributor: Contributions by /xivg/

using System;
using System.Security.Cryptography;
using System.Text;

namespace AcousticKitty.Echo;

internal static class DpapiProtector
{
	public static string Protect(string plaintext)
	{
		if (string.IsNullOrEmpty(plaintext))
		{
			return string.Empty;
		}

		var bytes = Encoding.UTF8.GetBytes(plaintext);
		var encrypted = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
		return Convert.ToBase64String(encrypted);
	}

	public static string Unprotect(string ciphertext)
	{
		if (string.IsNullOrEmpty(ciphertext))
		{
			return string.Empty;
		}

		var encrypted = Convert.FromBase64String(ciphertext);
		var bytes = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
		return Encoding.UTF8.GetString(bytes);
	}
}
