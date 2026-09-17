// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 https://echo-unvaulted.net
// SPDX-FileType: SOURCE
// SPDX-FileContributor: Contributions by /xivg/

using System;
using System.IO;
using System.Numerics;
using AcousticKitty.Windows.Tabs;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace AcousticKitty.Windows;

public enum MainWindowTab
{
	Nearby,
	Queues,
	Database,
	Settings,
}

public sealed class MainWindow : Window, IDisposable
{
	private const float SidebarWidth = 140f;
	private const float LogoSize = 96f;
	private const float TabPaddingY = 6f;

	private readonly string logoPath;
	private readonly FileDialogManager fileDialogManager = new();
	private readonly NearbyTab nearbyTab;
	private readonly QueuesTab queuesTab;
	private readonly DatabaseTab databaseTab;
	private readonly SettingsTab settingsTab;

	public MainWindowTab SelectedTab { get; set; } = MainWindowTab.Nearby;

	private MainWindowTab? lastDrawnTab;

	public MainWindow(Plugin plugin)
		: base(
			"Acoustic Kitty##AcousticKittyMain",
			ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
	{
		this.SizeConstraints = new WindowSizeConstraints
		{
			MinimumSize = new Vector2(460, 380),
			MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
		};

		this.logoPath = Path.Combine(
			Plugin.PluginInterface.AssemblyLocation.DirectoryName!, "images", "logo.png");
		this.nearbyTab = new NearbyTab(plugin, this.fileDialogManager);
		this.queuesTab = new QueuesTab(plugin);
		this.databaseTab = new DatabaseTab(plugin);
		this.settingsTab = new SettingsTab(plugin);
	}

	public void Dispose()
	{
	}

	public override void Draw()
	{
		this.fileDialogManager.Draw();

		using var table = ImRaii.Table("AcousticKittyLayout", 2, ImGuiTableFlags.BordersInnerV);
		if (!table)
		{
			return;
		}

		ImGui.TableSetupColumn("##Sidebar", ImGuiTableColumnFlags.WidthFixed, SidebarWidth);
		ImGui.TableNextColumn();

		using (ImRaii.Child(
			"##AcousticKittySidebar", Vector2.Zero, false, ImGuiWindowFlags.NoDecoration))
		{
			this.DrawLogo();
			ImGui.Spacing();
			ImGui.Separator();
			ImGui.Spacing();

			using (ImRaii.PushStyle(ImGuiStyleVar.SelectableTextAlign, new Vector2(0.5f, 0.5f)))
			{
				this.DrawTab("Nearby", MainWindowTab.Nearby);
				this.DrawTab("Queues", MainWindowTab.Queues);
				this.DrawTab("Database", MainWindowTab.Database);
				this.DrawTab("Settings", MainWindowTab.Settings);
			}
		}

		ImGui.TableNextColumn();
		using (ImRaii.Child("##AcousticKittyContent", Vector2.Zero, false))
		{
			var justSelected = this.lastDrawnTab != this.SelectedTab;
			this.lastDrawnTab = this.SelectedTab;

			switch (this.SelectedTab)
			{
				case MainWindowTab.Nearby:
					this.nearbyTab.Draw(justSelected);
					break;
				case MainWindowTab.Queues:
					this.queuesTab.Draw();
					break;
				case MainWindowTab.Database:
					this.databaseTab.Draw(justSelected);
					break;
				case MainWindowTab.Settings:
					this.settingsTab.Draw();
					break;
			}
		}
	}

	private void DrawTab(string label, MainWindowTab tab)
	{
		var height = ImGui.GetTextLineHeight() + (TabPaddingY * 2);
		var size = new Vector2(0, height);
		if (ImGui.Selectable(label, this.SelectedTab == tab, ImGuiSelectableFlags.None, size))
		{
			this.SelectedTab = tab;
		}
	}

	private void DrawLogo()
	{
		var texture = Plugin.TextureProvider.GetFromFile(this.logoPath).GetWrapOrDefault();
		if (texture == null)
		{
			return;
		}

		var offset = MathF.Max(0f, (ImGui.GetContentRegionAvail().X - LogoSize) / 2f);
		ImGui.SetCursorPosX(ImGui.GetCursorPosX() + offset);
		ImGui.Image(texture.Handle, new Vector2(LogoSize, LogoSize));
	}
}
