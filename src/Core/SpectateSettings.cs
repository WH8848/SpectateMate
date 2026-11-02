using System;
using Godot;
using STS2RitsuLib;
using STS2RitsuLib.Data;
using STS2RitsuLib.Settings;
using STS2RitsuLib.Utils.Persistence;

namespace SpectateMate.Core;

/// <summary>持久化的观战设置。</summary>
public sealed class SpectateSettingsData
{
    public string ExitKey { get; set; } = nameof(Key.Escape);

    /// <summary>下一回合开始时自动退出观战。默认关闭。</summary>
    public bool AutoExitNextTurn { get; set; }
}

/// <summary>
/// 观战设置：数据槽 + RitsuLib 设置页。
///
/// 设置页注册后会出现在 RitsuLib 的设置入口里（主菜单 patch notes 区域和跑局暂停菜单），
/// 玩家可以直接录制想用的退出键，值通过 ModSettingsValueBinding 写进 ModDataStore。
/// </summary>
public static class SpectateSettings
{
    public const string DataKey = "settings";
    public const string FileName = "spectate_settings.json";

    private static ModDataStore? _store;
    private static SpectateSettingsData _fallback = new();

    public static SpectateSettingsData Current
    {
        get
        {
            try
            {
                return _store?.Get<SpectateSettingsData>(DataKey) ?? _fallback;
            }
            catch
            {
                return _fallback;
            }
        }
    }

    /// <summary>下一回合开始时是否自动退出观战。</summary>
    public static bool AutoExitNextTurn
    {
        get
        {
            try
            {
                return Current.AutoExitNextTurn;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>退出观战用的键。解析失败时退回 Esc。</summary>
    public static Key ExitKey
    {
        get
        {
            string raw = Current.ExitKey;
            return Enum.TryParse(raw, out Key key) && key != Key.None ? key : Key.Escape;
        }
    }

    public static void Initialize()
    {
        try
        {
            _store = ModDataStore.For(ModEntry.ModId);

            using (RitsuLibFramework.BeginModDataRegistration(ModEntry.ModId, false))
            {
                _store.Register<SpectateSettingsData>(
                    DataKey,
                    FileName,
                    SaveScope.Global,
                    () => new SpectateSettingsData(),
                    true);
            }

            ModDataStore? store = _store;
            _fallback = store == null
                ? new SpectateSettingsData()
                : store.Get<SpectateSettingsData>(DataKey) ?? new SpectateSettingsData();

            RegisterPage();
        }
        catch (Exception e)
        {
            ModEntry.Log.Warn($"[{ModEntry.ModId}] settings init failed: {e.Message}");
        }
    }

    private static void RegisterPage()
    {
        RitsuLibFramework.RegisterModSettings(ModEntry.ModId, page => page
            .WithTitle(ModSettingsText.Literal("观战"))
            .WithModDisplayName(ModSettingsText.Literal("（功能）右键队友头像观战"))
            .AddSection("general", section => section
                .WithTitle(ModSettingsText.Literal("通用"))
                .AddKeyBinding(
                    "exit_spectate_key",
                    ModSettingsText.Literal("退出观战"),
                    new ModSettingsValueBinding<SpectateSettingsData, string>(
                        ModEntry.ModId,
                        DataKey,
                        SaveScope.Global,
                        s => s.ExitKey,
                        (s, value) => s.ExitKey = value),
                    allowModifierCombos: true,
                    allowModifierOnly: false,
                    distinguishModifierSides: false,
                    description: ModSettingsText.Literal("观战时按下这个键退出，默认 Esc。"))
                .AddToggle(
                    "auto_exit_next_turn",
                    ModSettingsText.Literal("下回合自动退出观战"),
                    new ModSettingsValueBinding<SpectateSettingsData, bool>(
                        ModEntry.ModId,
                        DataKey,
                        SaveScope.Global,
                        s => s.AutoExitNextTurn,
                        (s, value) => s.AutoExitNextTurn = value),
                    description: ModSettingsText.Literal(
                        "开启后，轮到你行动的瞬间自动结束观战，回到自己的视角。默认关闭。"))));
    }
}
