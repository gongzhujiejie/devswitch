// 文件用途：DevSwitch 命令转发器（shim）。被复制成 shims\<cmd>.exe（如 java.exe、mvn.cmd 的伴生 shim）。
//           运行时自定位 dataRoot，按固定优先级在 current\<type>\bin 下找到真实可执行并原样转发参数与退出码。
// 创建/修改日期：2026-06-11
// 语言版本要求：C++20（MinGW c++2a），纯 Win32，静态编译，启动开销极小。
// 依赖库：kernel32（CreateProcessW / GetModuleFileNameW 等），shell32 不需要。
// NOTE: 合法授权学习使用，仅限本地环境。
//   设计要点（为何这样写）：
//   1) PATH 只放一个 shims 目录即可覆盖 java/mvn/node/go 等全部命令，根治系统 PATH 2047 字符上限。
//   2) shim 不依赖任何环境变量定位 dataRoot——从自身 exe 路径上溯，避免环境漂移导致找不到目标。
//   3) 切换 SDK 只改 current junction 指向，shim 与 PATH 全程不变，因此切换无需写注册表、无需提权、极快。
//   4) 交互式程序（jshell、node REPL）依赖继承标准句柄与同控制台，这里不重定向、不开新窗口，保证可交互。

#ifndef UNICODE
#define UNICODE
#endif
#ifndef _UNICODE
#define _UNICODE
#endif

#include <windows.h>

#include <string>
#include <vector>
#include <cwctype>

namespace {

// 向 stderr 写一行诊断信息。用 WriteFile 而非 WriteConsoleW：后者在 stderr 被重定向为管道/文件时会失败，
// 导致错误信息丢失。WriteFile 对控制台与重定向句柄都有效。宽字符先转 UTF-8 再写。
void WriteErr(const std::wstring& text) {
    HANDLE h = GetStdHandle(STD_ERROR_HANDLE);
    if (h == nullptr || h == INVALID_HANDLE_VALUE) {
        return;
    }
    int bytes = WideCharToMultiByte(CP_UTF8, 0, text.c_str(), -1, nullptr, 0, nullptr, nullptr);
    if (bytes <= 1) {
        return;
    }
    std::vector<char> buf(static_cast<std::size_t>(bytes));
    WideCharToMultiByte(CP_UTF8, 0, text.c_str(), -1, buf.data(), bytes, nullptr, nullptr);
    DWORD written = 0;
    // bytes 含结尾 NUL，写出时去掉。
    WriteFile(h, buf.data(), static_cast<DWORD>(bytes - 1), &written, nullptr);
}

// 取自身可执行完整路径（宽字符）。失败返回空串。
std::wstring GetSelfPath() {
    std::vector<wchar_t> buffer(MAX_PATH);
    for (;;) {
        DWORD written = GetModuleFileNameW(nullptr, buffer.data(), static_cast<DWORD>(buffer.size()));
        if (written == 0) {
            return std::wstring();
        }
        // 缓冲区不足时 written == size 且 last error 为 ERROR_INSUFFICIENT_BUFFER，扩容重试。
        if (written < buffer.size()) {
            return std::wstring(buffer.data(), written);
        }
        buffer.resize(buffer.size() * 2);
    }
}

// 是否为路径分隔符。
bool IsSep(wchar_t ch) {
    return ch == L'\\' || ch == L'/';
}

// 返回去掉最末一段后的父目录（不含尾分隔符）。无父目录时返回空串。
std::wstring ParentDir(const std::wstring& path) {
    std::wstring p = path;
    while (p.size() > 1 && IsSep(p.back())) {
        p.pop_back();
    }
    std::size_t pos = p.find_last_of(L"\\/");
    if (pos == std::wstring::npos) {
        return std::wstring();
    }
    return p.substr(0, pos);
}

// 取路径最末文件名段。
std::wstring FileName(const std::wstring& path) {
    std::wstring p = path;
    while (p.size() > 1 && IsSep(p.back())) {
        p.pop_back();
    }
    std::size_t pos = p.find_last_of(L"\\/");
    return pos == std::wstring::npos ? p : p.substr(pos + 1);
}

// 去掉扩展名，得到命令 stem（如 java.exe -> java）。仅去最后一个 '.' 之后部分。
std::wstring Stem(const std::wstring& name) {
    std::size_t pos = name.find_last_of(L'.');
    if (pos == std::wstring::npos || pos == 0) {
        return name;
    }
    return name.substr(0, pos);
}

// 拼接 parent\child。
std::wstring Combine(const std::wstring& parent, const std::wstring& child) {
    if (parent.empty()) {
        return child;
    }
    std::wstring p = parent;
    if (!IsSep(p.back())) {
        p.push_back(L'\\');
    }
    p += child;
    return p;
}

// 文件是否存在（且不是目录）。
bool FileExists(const std::wstring& path) {
    DWORD attr = GetFileAttributesW(path.c_str());
    return attr != INVALID_FILE_ATTRIBUTES && !(attr & FILE_ATTRIBUTE_DIRECTORY);
}

// 在 current\<type>\<subdir> 下按 stem + 常见可执行扩展探测真实目标。
// 命中返回完整路径，否则返回空串。
std::wstring ProbeIn(const std::wstring& dir, const std::wstring& stem) {
    // 扩展名优先级：.exe（真 PE，直接启动）> .cmd > .bat（批处理，需 cmd /c）。
    static const wchar_t* kExts[] = { L".exe", L".cmd", L".bat", L".com" };
    for (const wchar_t* ext : kExts) {
        std::wstring candidate = Combine(dir, stem + ext);
        if (FileExists(candidate)) {
            return candidate;
        }
    }
    return std::wstring();
}

// 解析真实目标：按 java/maven/node/go 固定优先级在 current\<type>\bin（node 在 current\node）下探测。
// 返回命中的完整路径；未命中返回空串。
std::wstring ResolveTarget(const std::wstring& dataRoot, const std::wstring& stem) {
    std::wstring current = Combine(dataRoot, L"current");

    // 候选目录顺序固定，与 helper 生成 shim 的探测顺序一致，避免歧义。
    std::vector<std::wstring> dirs = {
        Combine(Combine(current, L"java"), L"bin"),
        Combine(Combine(current, L"maven"), L"bin"),
        Combine(current, L"node"),
        Combine(Combine(current, L"go"), L"bin"),
    };

    for (const std::wstring& dir : dirs) {
        std::wstring hit = ProbeIn(dir, stem);
        if (!hit.empty()) {
            return hit;
        }
    }
    return std::wstring();
}

// 给路径加引号（若含空格且未加引号）。用于拼批处理命令行。
std::wstring QuoteIfNeeded(const std::wstring& value) {
    if (value.empty()) {
        return L"\"\"";
    }
    bool hasSpace = value.find_first_of(L" \t") != std::wstring::npos;
    bool quoted = value.size() >= 2 && value.front() == L'"' && value.back() == L'"';
    if (hasSpace && !quoted) {
        return L"\"" + value + L"\"";
    }
    return value;
}

// 取本进程原始命令行中“第一个参数（程序名）之后”的剩余部分（保留原始引号与空格语义）。
// 这样转发给真实目标时，用户输入的参数一字不改。
std::wstring GetForwardedArgs() {
    const wchar_t* raw = GetCommandLineW();
    if (raw == nullptr) {
        return std::wstring();
    }

    const wchar_t* p = raw;
    // 跳过程序名：处理带引号与不带引号两种形式。
    if (*p == L'"') {
        ++p; // 跳过起始引号
        while (*p && *p != L'"') {
            ++p;
        }
        if (*p == L'"') {
            ++p; // 跳过结束引号
        }
    } else {
        while (*p && *p != L' ' && *p != L'\t') {
            ++p;
        }
    }

    // 跳过程序名后的空白，剩余即为参数串。
    while (*p == L' ' || *p == L'\t') {
        ++p;
    }
    return std::wstring(p);
}

} // namespace

int wmain() {
    std::wstring self = GetSelfPath();
    if (self.empty()) {
        // GetModuleFileName 失败极罕见，直接返回错误码。
        return 200;
    }

    // 自定位 dataRoot：self = dataRoot\shims\<cmd>.exe -> 上溯两级。
    std::wstring shimsDir = ParentDir(self);     // dataRoot\shims
    std::wstring dataRoot = ParentDir(shimsDir); // dataRoot
    if (dataRoot.empty()) {
        return 201;
    }

    std::wstring stem = Stem(FileName(self));
    if (stem.empty()) {
        return 202;
    }

    std::wstring target = ResolveTarget(dataRoot, stem);
    if (target.empty()) {
        // 未切换对应 SDK 或目标缺失：向 stderr 给出可诊断信息，返回 9009（与“命令找不到”惯例一致）。
        WriteErr(L"DevSwitch shim: target not found for '" + stem + L"'. Switch the SDK first.\n");
        return 9009;
    }

    std::wstring args = GetForwardedArgs();

    // 组装子进程命令行。.cmd/.bat 必须经 cmd.exe /c 运行（不能直接 CreateProcess 批处理）。
    std::wstring appName;          // lpApplicationName，可为空
    std::wstring commandLine;      // lpCommandLine，可写缓冲
    std::wstring ext;
    {
        std::size_t pos = target.find_last_of(L'.');
        if (pos != std::wstring::npos) {
            ext = target.substr(pos);
            for (wchar_t& ch : ext) {
                ch = static_cast<wchar_t>(towlower(ch));
            }
        }
    }

    bool isBatch = (ext == L".cmd" || ext == L".bat");
    if (isBatch) {
        // 用 ComSpec 定位 cmd.exe；命令行形如：cmd.exe /c "target" args
        // lpApplicationName 传 nullptr，让系统按命令行首段（cmd.exe）解析，避免 ComSpec 路径异常导致 error 2。
        wchar_t comspec[MAX_PATH];
        DWORD n = GetEnvironmentVariableW(L"ComSpec", comspec, MAX_PATH);
        std::wstring cmdExe = (n > 0 && n < MAX_PATH) ? std::wstring(comspec, n) : std::wstring(L"cmd.exe");
        appName.clear();
        commandLine = QuoteIfNeeded(cmdExe) + L" /c " + QuoteIfNeeded(target);
        if (!args.empty()) {
            commandLine += L" " + args;
        }
    } else {
        // 真 PE：appName 指定真实目标，命令行首段填目标（约定俗成的 argv[0]），其后接转发参数。
        appName = target;
        commandLine = QuoteIfNeeded(target);
        if (!args.empty()) {
            commandLine += L" " + args;
        }
    }

    // CreateProcessW 要求 lpCommandLine 可写。
    std::vector<wchar_t> cmdBuffer(commandLine.begin(), commandLine.end());
    cmdBuffer.push_back(L'\0');

    STARTUPINFOW si{};
    si.cb = sizeof(si);
    // 不设 STARTF_USESTDHANDLES：默认继承父进程标准句柄，保证交互式 REPL 正常读写控制台。

    PROCESS_INFORMATION pi{};
    BOOL ok = CreateProcessW(
        appName.empty() ? nullptr : appName.c_str(),
        cmdBuffer.data(),
        nullptr,
        nullptr,
        TRUE,          // 继承句柄：标准输入/输出/错误透传给子进程
        0,             // 不开新窗口、不脱离控制台
        nullptr,       // 继承环境块
        nullptr,       // 继承当前工作目录
        &si,
        &pi);

    if (!ok) {
        DWORD err = GetLastError();
        WriteErr(L"DevSwitch shim: failed to start target '" + target + L"' (error " + std::to_wstring(err) + L").\n");
        return 203;
    }

    // 等待子进程结束并透传退出码，保证调用方脚本能正确判断成败。
    WaitForSingleObject(pi.hProcess, INFINITE);
    DWORD exitCode = 0;
    GetExitCodeProcess(pi.hProcess, &exitCode);
    CloseHandle(pi.hThread);
    CloseHandle(pi.hProcess);

    return static_cast<int>(exitCode);
}
