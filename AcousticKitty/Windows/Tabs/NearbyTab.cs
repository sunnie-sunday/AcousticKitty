// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 https://echo-unvaulted.net
// SPDX-FileType: SOURCE
// SPDX-FileContributor: Contributions by /xivg/

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using AcousticKitty.Character;
using AcousticKitty.Windows.Views;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Utility.Raii;

namespace AcousticKitty.Windows.Tabs;

internal sealed class NearbyTab(Plugin plugin, FileDialogManager fileDialogManager)
{
	private volatile IReadOnlyList<NearbyMemberViewModel> displayedMembers =
		Array.Empty<NearbyMemberViewModel>();

	private string nameFilter = string.Empty;
	private readonly FilterCache<NearbyMemberViewModel> filterCache = new();

	private readonly Dictionary<string, string> lodestoneIdInputs = new();

	private volatile string? importValidationError;
	private volatile bool isImporting;

	public void Draw(bool justSelected)
	{
		var nearby = plugin.NearbyCharactersService;

		if (justSelected)
		{
			this.RefreshSnapshot(nearby);
		}

		this.DrawFilterAndRefresh(nearby);

		if (this.importValidationError != null)
		{
			ImGui.TextColored(ImGuiColors.ErrorForeground, this.importValidationError);
		}

		ImGui.Spacing();

		if (this.displayedMembers.Count == 0)
		{
			ImGui.TextWrapped("No nearby players found.");
			return;
		}

		var filtered = this.filterCache.Filter(
			this.displayedMembers, this.nameFilter, member => member.KnownCharacter.Data.Name);

		if (filtered.Count == 0)
		{
			ImGui.TextWrapped("No characters match this filter.");
			return;
		}

		using (ImRaii.Child("CurrentlyNearbyResults", Vector2.Zero, true))
		{
			foreach (var member in filtered)
			{
				NearbyMemberRow.Draw(plugin, nearby, member, this.lodestoneIdInputs);
				ImGui.Separator();
			}
		}
	}

	private void DrawFilterAndRefresh(NearbyCharactersService nearby)
	{
		FilterAndRefreshRow.Draw(
			ref this.nameFilter,
			onFilterChanged: null,
			refreshEnabled: true,
			onRefresh: () => this.RefreshSnapshot(nearby),
			refreshIdSuffix: "NearbyRefresh",
			busyMessage: this.isImporting ? "Importing..." : null,
			extraButton: ("Import", this.OnImportClicked));
	}

	private void OnImportClicked()
	{
		if (this.isImporting)
		{
			return;
		}

		fileDialogManager.OpenFileDialog(
			"Import Nearby Characters",
			".json",
			(isOk, path) =>
			{
				if (isOk && !this.isImporting)
				{
					this.isImporting = true;
					_ = Task.Run(() => this.TryImport(path));
				}
			});
	}

	private void TryImport(string path)
	{
		try
		{
			var imported = PcsFile.Load(path);
			if (imported.Count == 0)
			{
				this.importValidationError = "The file contained no characters.";
				return;
			}

			this.importValidationError = null;
			var nearby = plugin.NearbyCharactersService;
			nearby.ImportSnapshots(imported);
			this.RefreshSnapshot(nearby);
		}
		catch (PcsFileException ex)
		{
			this.importValidationError = ex.Message;
		}
		catch (Exception ex)
		{
			Plugin.Log.Error(ex, "Failed to import nearby characters.");
			this.importValidationError = "The import failed, see the log for details.";
		}
		finally
		{
			this.isImporting = false;
		}
	}

	private void RefreshSnapshot(NearbyCharactersService nearby) =>
		this.displayedMembers = nearby.CurrentlyNearby;
}
