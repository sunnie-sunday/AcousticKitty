// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 https://echo-unvaulted.net
// SPDX-FileType: SOURCE
// SPDX-FileContributor: Contributions by /xivg/

using System;
using AcousticKitty.Character;
using AcousticKitty.Echo;
using AcousticKitty.Windows.Views;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;

namespace AcousticKitty.Windows.Tabs;

internal sealed class SettingsTab(Plugin plugin)
{
	private string proxyHostBuffer = plugin.Configuration.EchoProxyHost;
	private string proxyPortBuffer = plugin.Configuration.EchoProxyPort.ToString();
	private string proxyUsernameBuffer = plugin.Configuration.EchoProxyUsername;
	private string proxyPasswordBuffer = plugin.Configuration.EchoProxyPassword;
	private string? proxyValidationError;

	public void Draw()
	{
		var configuration = plugin.Configuration;

		ImGui.TextUnformatted("Lodestone connection");

		var userAgentBuffer = configuration.LodestoneUserAgent;
		if (ImGui.InputText("User-Agent", ref userAgentBuffer, 256))
		{
			configuration.LodestoneUserAgent = userAgentBuffer;
			configuration.Save();
		}

		ImGui.BeginDisabled();
		ImGui.TextWrapped(
			"Sent only to the Lodestone, never to the Echo API below. Left blank, falls back to " +
			$"\"{Configuration.DefaultLodestoneUserAgent}\".");
		ImGui.EndDisabled();

		ImGui.Spacing();

		ImGui.TextUnformatted("Echo API connection");
		SettingsTab.DrawProxyModeRadio(
			"##EchoProxyMode",
			configuration.EchoProxyMode,
			mode =>
			{
				configuration.EchoProxyMode = mode;
				configuration.Save();
			});

		if (configuration.EchoProxyMode == ProxyMode.Direct)
		{
			ImGui.TextColored(
				ImGuiColors.WarningForeground,
				"A SOCKS5 proxy is recommended for Echo API traffic.");
		}

		ImGui.Spacing();
		ImGui.Separator();
		ImGui.Spacing();

		var usesProxy = configuration.EchoProxyMode == ProxyMode.Socks5;

		ImGui.TextWrapped("SOCKS5 proxy (Echo API only - Lodestone traffic always connects direct)");
		ImGui.Spacing();

		if (!usesProxy)
		{
			ImGui.BeginDisabled();
		}

		ImGui.InputText("Host", ref this.proxyHostBuffer, 256);
		ImGui.InputText("Port", ref this.proxyPortBuffer, 6, ImGuiInputTextFlags.CharsDecimal);
		ImGui.InputText("Username (optional)", ref this.proxyUsernameBuffer, 128);
		ImGui.InputText(
			"Password (optional)", ref this.proxyPasswordBuffer, 128, ImGuiInputTextFlags.Password);

		if (this.proxyValidationError != null)
		{
			ImGui.TextColored(ImGuiColors.ErrorForeground, this.proxyValidationError);
		}

		if (ImGui.Button("Save proxy settings"))
		{
			this.TrySaveProxySettings(configuration);
		}

		if (!usesProxy)
		{
			ImGui.EndDisabled();
		}

		ImGui.Spacing();
		ImGui.Separator();
		ImGui.Spacing();

		ImGui.TextWrapped("Echo API");
		ImGui.Spacing();

		ImGui.BeginDisabled();
		var apiHostBuffer = configuration.EchoApiHost;
		ImGui.InputText("API host", ref apiHostBuffer, 256);
		var protocolVersionBuffer = configuration.EchoProtocolVersion.ToString();
		ImGui.InputText(
			"API protocol version", ref protocolVersionBuffer, 6, ImGuiInputTextFlags.CharsDecimal);
		ImGui.EndDisabled();

		ImGui.Spacing();

		var pluginVersionBuffer = configuration.EchoPluginVersion;
		if (ImGui.InputText("API plugin version", ref pluginVersionBuffer, 32))
		{
			configuration.EchoPluginVersion = pluginVersionBuffer;
			configuration.Save();
		}

		ImGui.Spacing();
		ImGui.Separator();
		ImGui.Spacing();

		ImGui.TextWrapped("Choose how the Queue tab resolves nearby players on the Lodestone.");
		ImGui.Spacing();

		if (ImGui.RadioButton(
			"Manual", configuration.NearbySearchMode == NearbySearchMode.Manual))
		{
			configuration.NearbySearchMode = NearbySearchMode.Manual;
			configuration.Save();
		}

		ImGui.SameLine();
		if (ImGui.RadioButton("Auto", configuration.NearbySearchMode == NearbySearchMode.Auto))
		{
			configuration.NearbySearchMode = NearbySearchMode.Auto;
			configuration.Save();
		}

		ImGui.BeginDisabled();
		ImGui.TextWrapped(
			"Manual looks up characters when using \"Start Lodestone queue\" on the Queue tab.");
		ImGui.TextWrapped("Auto looks up every nearby player passively in the background.");
		ImGui.EndDisabled();

		ImGui.Spacing();
		ImGui.Separator();
		ImGui.Spacing();

		ImGui.TextWrapped(
			"Choose how the Queue tab verifies confirmed matches with the Echo API - independent " +
			"of the Lodestone lookup mode above.");
		ImGui.Spacing();

		if (ImGui.RadioButton(
			"Manual##EchoQueueMode", configuration.EchoQueueMode == EchoQueueMode.Manual))
		{
			configuration.EchoQueueMode = EchoQueueMode.Manual;
			configuration.Save();
		}

		ImGui.SameLine();
		if (ImGui.RadioButton(
			"Auto##EchoQueueMode", configuration.EchoQueueMode == EchoQueueMode.Auto))
		{
			configuration.EchoQueueMode = EchoQueueMode.Auto;
			configuration.Save();
		}

		ImGui.BeginDisabled();
		ImGui.TextWrapped(
			"Manually verifies confirmed matches when using \"Start Echo queue\" on the Queue tab.");
		ImGui.TextWrapped("Auto verifies every confirmed match automatically in the background.");
		ImGui.EndDisabled();
	}

	private static void DrawProxyModeRadio(
		string idSuffix, ProxyMode current, Action<ProxyMode> setMode)
	{
		if (ImGui.RadioButton($"Direct (HTTPS){idSuffix}", current == ProxyMode.Direct))
		{
			setMode(ProxyMode.Direct);
		}

		ImGui.SameLine();
		if (ImGui.RadioButton($"Proxy (SOCKS5){idSuffix}", current == ProxyMode.Socks5))
		{
			setMode(ProxyMode.Socks5);
		}
	}

	private void TrySaveProxySettings(Configuration configuration)
	{
		if (string.IsNullOrWhiteSpace(this.proxyHostBuffer))
		{
			this.proxyValidationError = "Host is required.";
			return;
		}

		if (!int.TryParse(this.proxyPortBuffer, out var port) || port is < 1 or > 65535)
		{
			this.proxyValidationError = "Port must be a number between 1 and 65535.";
			return;
		}

		this.proxyValidationError = null;
		configuration.EchoProxyHost = this.proxyHostBuffer.Trim();
		configuration.EchoProxyPort = port;
		configuration.EchoProxyUsername = this.proxyUsernameBuffer.Trim();
		configuration.EchoProxyPassword = this.proxyPasswordBuffer;
		configuration.Save();
	}
}
