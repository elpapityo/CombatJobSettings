using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace CombatJobSettings;

internal sealed class MainCjsWindow : Window
{
    private readonly Plugin plugin;

    internal MainCjsWindow(Plugin plugin)
        : base("Combat Job Settings V0.1.0 ｜ 適用：未適用###cjs_main")
    {
        this.plugin = plugin;
        Size = new Vector2(340, 520);
        SizeCondition = ImGuiCond.FirstUseEver;
        AllowPinning = true;
        AllowClickthrough = true;
        ShowCloseButton = true;
        IsOpen = false;
    }

    public override void Draw() => plugin.DrawMainContent();
}

internal sealed class MiniCjsWindow : Window
{
    private readonly Plugin plugin;

    internal MiniCjsWindow(Plugin plugin)
        : base("CJS Mini###cjs_mini")
    {
        Flags |= ImGuiWindowFlags.AlwaysAutoResize;
        this.plugin = plugin;
        AllowPinning = true;
        AllowClickthrough = true;
        ShowCloseButton = true;
        IsOpen = false;
    }

    public override void PreDraw()
    {
        var style = ImGui.GetStyle();

        var bg = style.Colors[(int)ImGuiCol.WindowBg];
        bg.W = plugin.MiniBackgroundOpacity;
        ImGui.PushStyleColor(ImGuiCol.WindowBg, bg);

        var title = style.Colors[(int)ImGuiCol.TitleBg];
        title.W = plugin.MiniTitleOpacity;
        ImGui.PushStyleColor(ImGuiCol.TitleBg, title);

        var titleActive = style.Colors[(int)ImGuiCol.TitleBgActive];
        titleActive.W = plugin.MiniTitleOpacity;
        ImGui.PushStyleColor(ImGuiCol.TitleBgActive, titleActive);

        var titleCollapsed = style.Colors[(int)ImGuiCol.TitleBgCollapsed];
        titleCollapsed.W = plugin.MiniTitleOpacity;
        ImGui.PushStyleColor(ImGuiCol.TitleBgCollapsed, titleCollapsed);
    }

    public override void Draw() => plugin.DrawMiniContent();

    public override void PostDraw()
    {
        ImGui.PopStyleColor(4);
    }
}
