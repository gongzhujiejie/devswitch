# Use WinUI 3, XAML, and MVVM for the GUI frontend

DevSwitch is a Windows-native, GUI-first tool, so its frontend uses C# with WinUI 3, XAML, and MVVM rather than Electron, WebView, or a web frontend stack. This keeps startup time and memory usage aligned with the product goals, gives the app native Windows controls and styling, and lets the C++ helper remain focused on low-level system operations.
