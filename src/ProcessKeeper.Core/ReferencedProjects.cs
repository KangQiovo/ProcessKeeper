namespace ProcessKeeper.Core;

public sealed record ReferencedProject(string Name, string Description, string Url);

/// <summary>Source projects for directly used and bundled open-source components.
/// Related .NET packages are listed by name under their common upstream project.</summary>
public static class ReferencedProjects
{
    public static IReadOnlyList<ReferencedProject> All => new[]
    {
        new ReferencedProject("WinUI Gallery", L.T("原生控件与页面交互的参考示例。"), "https://github.com/microsoft/WinUI-Gallery"),
        new ReferencedProject("WinUI 3", L.T("现代界面的导航、列表、对话框与提示组件。"), "https://github.com/microsoft/microsoft-ui-xaml"),
        new ReferencedProject("Windows App SDK", L.T("窗口、文件选择器与现代 Windows 运行组件。"), "https://github.com/microsoft/WindowsAppSDK"),
        new ReferencedProject("C#/WinRT", L.T("C# 与 Windows Runtime 接口之间的桥接。"), "https://github.com/microsoft/CsWinRT"),
        new ReferencedProject("WPF UI / WPF-UI.Abstractions", L.T("兼容界面的 Fluent 控件、图标与主题资源。"), "https://github.com/lepoco/wpfui"),
        new ReferencedProject("VirtualizingWrapPanel", L.T("WPF UI 内含的虚拟化布局组件。"), "https://github.com/sbaeumlisberger/VirtualizingWrapPanel"),
        new ReferencedProject("Fluent UI System Icons", L.T("WPF UI 内含的 Fluent 系统图标。"), "https://github.com/microsoft/fluentui-system-icons"),
        new ReferencedProject("WPF", L.T("兼容界面的基础框架与 WPF UI 中采用的控件实现。"), "https://github.com/dotnet/wpf"),
        new ReferencedProject(".NET Runtime", L.T("现代版运行时与通用基础类库。"), "https://github.com/dotnet/runtime"),
        new ReferencedProject("System.Text.Json / System.Text.Encodings.Web", L.T("规则、设置、记录与通信数据的 JSON 处理。"), "https://github.com/dotnet/runtime/tree/main/src/libraries/System.Text.Json"),
        new ReferencedProject("System.Memory / System.Buffers / System.Runtime.CompilerServices.Unsafe", L.T("兼容运行环境中的内存、缓冲区与底层访问支持。"), "https://github.com/dotnet/runtime/tree/main/src/libraries"),
        new ReferencedProject("Microsoft.Bcl.AsyncInterfaces / System.Threading.Tasks.Extensions / System.ValueTuple", L.T("兼容运行环境中的异步接口、任务与元组支持。"), "https://github.com/dotnet/runtime/tree/main/src/libraries"),
        new ReferencedProject("System.Numerics.Vectors / System.Numerics.Tensors", L.T("基础数值类库；张量组件随 Windows App SDK 附带。"), "https://github.com/dotnet/runtime/tree/main/src/libraries"),
        new ReferencedProject("ONNX Runtime", L.T("随 Windows App SDK 附带；本应用不使用模型推理。"), "https://github.com/microsoft/onnxruntime"),
        new ReferencedProject("Brotli", L.T("随 .NET 运行时提供的压缩支持。"), "https://github.com/google/brotli"),
        new ReferencedProject("zlib", L.T("随 .NET 运行时提供的压缩与归档支持。"), "https://github.com/madler/zlib"),
        new ReferencedProject("Simple Icons", L.T("GitHub 与 B 站平台图标。"), "https://github.com/simple-icons/simple-icons"),
        new ReferencedProject("Lawnicons", L.T("酷安平台图标。"), "https://github.com/LawnchairLauncher/lawnicons"),
        new ReferencedProject("Plain Craft Launcher", L.T("内存优化与下载功能的行为参考；独立实现，未包含其源代码。"), "https://github.com/Meloong-Git/PCL"),
        new ReferencedProject("PCL Community", L.T("工具页面交互的行为参考；独立实现，未包含其源代码。"), "https://github.com/PCL-Community/PCL-CE")
    };
}
