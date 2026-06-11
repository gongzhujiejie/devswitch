// 文件用途：DevSwitch 外部更新器（updater）。由主程序 DevSwitch.App 在“准备就绪的新版本已下载解压完毕”后拉起，
//           随后主程序自身退出；updater 等主程序进程完全结束，再把新版文件覆盖到安装目录，最后重启主程序。
//           因为正在运行的进程无法覆盖自身可执行文件，所以“覆盖 + 重启”必须由这个独立的外部进程完成。
// 创建/修改日期：2026-06-11
// 语言版本要求：C++20（MinGW，编译参数：-std=c++2a -O2 -municode -static），纯 Win32，无第三方依赖。
// 依赖库：kernel32（进程/文件/目录 API）、shell32（CommandLineToArgvW，主代理编译时加 -lshell32）。
// NOTE: 合法授权学习使用，仅限本地环境。
//   设计要点（为何这样写）：
//   1) 必须等主程序进程结束并额外缓冲 500ms，确保其打开的 dll / 日志等文件句柄完全释放，否则 CopyFileW 会因占用失败。
//   2) 覆盖式复制（CopyFileW 第三参数 FALSE）只“增量覆盖”，绝不删除目标里 source 没有的文件，避免误删用户内容。
//   3) 顶层 data 目录承载用户数据（SDK、配置、日志、shims、current 链接），整目录跳过，绝不触碰。
//   4) updater 自身正在运行无法被覆盖，按文件名（不区分大小写）在任意层级跳过。
//   5) 单文件复制失败做有限重试（应对短暂占用），多次仍失败则记录并继续其余文件，最终用退出码反映“部分失败”，
//      尽量保证一次更新能落地最多的文件，而不是一遇占用就整体中止。

#ifndef UNICODE
#define UNICODE
#endif
#ifndef _UNICODE
#define _UNICODE
#endif

#include <windows.h>
#include <shellapi.h>  // CommandLineToArgvW

#include <string>
#include <vector>
#include <cwctype>

namespace {

// ============================ 退出码约定 ============================
// 0：全部文件成功覆盖并成功重启（或重启已尽力）。
// 1：命令行参数错误（缺少必需参数 / 参数格式不合法）。
// 2：有文件复制失败但已尽力（其余文件已覆盖），属“部分失败”。
// 3：致命错误（如 source 目录不存在，无法继续整个更新）。
constexpr int kExitOk = 0;
constexpr int kExitBadArgs = 1;
constexpr int kExitPartialFail = 2;
constexpr int kExitFatal = 3;

// 等待主程序退出的超时上限（毫秒）。超过则不再死等，直接进入覆盖流程。
constexpr DWORD kWaitTimeoutMs = 30000;
// 主程序退出后额外缓冲时间（毫秒），等待其文件句柄被系统彻底回收。
constexpr DWORD kPostExitSleepMs = 500;
// 单文件复制失败重试次数与每次间隔（毫秒），应对文件仍被短暂占用的竞态。
constexpr int kCopyRetry = 3;
constexpr DWORD kCopyRetryDelayMs = 300;

// 日志文件路径（来自 --log，可空）。为简化在各函数间传递，集中保存于此。
std::wstring g_logPath;

// ---------------------------------------------------------------
// 向 --log 指定的文件追加写入一行（UTF-8 编码）。未提供 --log 时静默跳过。
// 用 CreateFileW + FILE_APPEND_DATA 追加，绝不截断已有日志；任何失败都不抛、不中断主流程。
// 参数 text：要写入的宽字符串（函数内部自动追加换行）。
// ---------------------------------------------------------------
void LogLine(const std::wstring& text) {
    if (g_logPath.empty()) {
        return;
    }
    HANDLE h = CreateFileW(
        g_logPath.c_str(),
        FILE_APPEND_DATA,                       // 仅追加权限，配合下方定位文件尾
        FILE_SHARE_READ | FILE_SHARE_WRITE,     // 允许他人同时读，便于实时观察
        nullptr,
        OPEN_ALWAYS,                            // 不存在则新建，存在则打开
        FILE_ATTRIBUTE_NORMAL,
        nullptr);
    if (h == INVALID_HANDLE_VALUE) {
        return;
    }
    // 移动到文件末尾，确保追加而非覆盖。
    SetFilePointer(h, 0, nullptr, FILE_END);

    std::wstring line = text;
    line.push_back(L'\r');
    line.push_back(L'\n');

    // 宽字符转 UTF-8 再落盘，保证中文日志不乱码、跨编辑器可读。
    int bytes = WideCharToMultiByte(CP_UTF8, 0, line.c_str(), -1, nullptr, 0, nullptr, nullptr);
    if (bytes > 1) {
        std::vector<char> buf(static_cast<std::size_t>(bytes));
        WideCharToMultiByte(CP_UTF8, 0, line.c_str(), -1, buf.data(), bytes, nullptr, nullptr);
        DWORD written = 0;
        // bytes 含结尾 NUL，写出时去掉。
        WriteFile(h, buf.data(), static_cast<DWORD>(bytes - 1), &written, nullptr);
    }
    CloseHandle(h);
}

// 是否为路径分隔符。
bool IsSep(wchar_t ch) {
    return ch == L'\\' || ch == L'/';
}

// 去掉路径结尾的所有分隔符（保留至少一个字符，避免把根 "C:\" 削空出问题）。
std::wstring TrimTrailingSep(const std::wstring& path) {
    std::wstring p = path;
    while (p.size() > 1 && IsSep(p.back())) {
        p.pop_back();
    }
    return p;
}

// 拼接 parent\child，自动处理 parent 结尾分隔符。
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

// 不区分大小写比较两个宽字符串是否相等。用于文件名匹配（如跳过 updater 自身、识别 data 目录）。
bool EqualsIgnoreCase(const std::wstring& a, const std::wstring& b) {
    if (a.size() != b.size()) {
        return false;
    }
    for (std::size_t i = 0; i < a.size(); ++i) {
        if (towlower(a[i]) != towlower(b[i])) {
            return false;
        }
    }
    return true;
}

// 判断目录是否存在。
bool DirectoryExists(const std::wstring& path) {
    DWORD attr = GetFileAttributesW(path.c_str());
    return attr != INVALID_FILE_ATTRIBUTES && (attr & FILE_ATTRIBUTE_DIRECTORY);
}

// ---------------------------------------------------------------
// 递归创建目录（类似 mkdir -p）。逐级 CreateDirectoryW，已存在视为成功。
// 返回最终目录是否存在/创建成功。
// ---------------------------------------------------------------
bool EnsureDirectory(const std::wstring& path) {
    if (path.empty()) {
        return false;
    }
    if (DirectoryExists(path)) {
        return true;
    }
    // 先确保父目录存在，再创建自身，实现递归建链。
    std::wstring trimmed = TrimTrailingSep(path);
    std::size_t pos = trimmed.find_last_of(L"\\/");
    if (pos != std::wstring::npos && pos > 0) {
        std::wstring parent = trimmed.substr(0, pos);
        // 跳过盘符根（形如 "C:"），其本身无需创建。
        if (!(parent.size() == 2 && parent[1] == L':')) {
            EnsureDirectory(parent);
        }
    }
    if (CreateDirectoryW(trimmed.c_str(), nullptr)) {
        return true;
    }
    // 并发或竞态下可能已被创建，再判一次。
    return DirectoryExists(trimmed);
}

// ---------------------------------------------------------------
// 带重试的单文件覆盖复制。CopyFileW(src, dst, FALSE) 中 FALSE 表示“目标存在也覆盖”。
// 失败时最多重试 kCopyRetry 次（每次间隔 kCopyRetryDelayMs），应对文件仍被短暂占用的情况。
// 返回是否最终复制成功。
// ---------------------------------------------------------------
bool CopyFileWithRetry(const std::wstring& src, const std::wstring& dst) {
    for (int attempt = 0; attempt <= kCopyRetry; ++attempt) {
        if (attempt > 0) {
            // 非首次尝试前先等待，给占用方释放句柄的时间。
            Sleep(kCopyRetryDelayMs);
        }
        if (CopyFileW(src.c_str(), dst.c_str(), FALSE)) {
            return true;
        }
    }
    return false;
}

// ---------------------------------------------------------------
// 递归覆盖复制：把 source 目录树整体覆盖到 target，对应相对路径一一对应。
//   - source：本层源目录绝对路径。
//   - target：本层目标目录绝对路径（不存在会被创建）。
//   - isTopLevel：当前是否为最顶层调用（决定 data 目录跳过规则只对顶层生效）。
//   - selfName：updater 自身文件名（如 "DevSwitch.Updater.exe"），任意层级遇到都跳过。
//   - failedCount：输出参数，累计复制失败的文件数。
// 返回值：本层（含子层）是否“整体顺利”——仅用于内部传播；最终成败以 failedCount 判定。
//
// 跳过规则（绝不覆盖/删除）：
//   1) 顶层名为 data 的目录（不区分大小写）：整目录跳过——用户 SDK/配置/日志/shims/current 在此。
//   2) 文件名等于 selfName（不区分大小写）：跳过——updater 正在运行，无法覆盖自身。
// 注意：本函数只做“覆盖复制”，不会删除 target 中 source 不存在的文件。
// ---------------------------------------------------------------
bool CopyTreeOverwrite(const std::wstring& source,
                       const std::wstring& target,
                       bool isTopLevel,
                       const std::wstring& selfName,
                       int& failedCount) {
    // 确保目标目录存在（顶层 target 一般已存在，子目录可能需要现建）。
    if (!EnsureDirectory(target)) {
        LogLine(L"[ERROR] 无法创建目标目录: " + target);
        return false;
    }

    // 用 FindFirstFileW 枚举 source 下所有条目。通配符 "\*" 匹配全部文件与子目录。
    std::wstring pattern = Combine(source, L"*");
    WIN32_FIND_DATAW fd{};
    HANDLE hFind = FindFirstFileW(pattern.c_str(), &fd);
    if (hFind == INVALID_HANDLE_VALUE) {
        // source 为空目录或枚举失败：空目录是正常情况（目标目录已建好），直接返回成功。
        return true;
    }

    bool overallOk = true;
    do {
        std::wstring name = fd.cFileName;
        // 跳过当前目录 "." 与父目录 ".."，否则会无限递归。
        if (name == L"." || name == L"..") {
            continue;
        }

        bool isDir = (fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0;

        // 跳过规则 1：顶层 data 目录整体不动。
        if (isTopLevel && isDir && EqualsIgnoreCase(name, L"data")) {
            LogLine(L"[SKIP] 顶层用户数据目录: " + name);
            continue;
        }
        // 跳过规则 2：updater 自身（任意层级，按文件名）。
        if (!isDir && EqualsIgnoreCase(name, selfName)) {
            LogLine(L"[SKIP] updater 自身文件: " + name);
            continue;
        }

        std::wstring srcPath = Combine(source, name);
        std::wstring dstPath = Combine(target, name);

        if (isDir) {
            // 子目录：递归处理（子层不再是顶层，data 跳过规则对深层 data 不生效）。
            if (!CopyTreeOverwrite(srcPath, dstPath, /*isTopLevel*/ false, selfName, failedCount)) {
                overallOk = false;
            }
        } else {
            // 普通文件：带重试覆盖复制。
            if (CopyFileWithRetry(srcPath, dstPath)) {
                LogLine(L"[COPY] " + dstPath);
            } else {
                DWORD err = GetLastError();
                ++failedCount;
                overallOk = false;
                LogLine(L"[FAIL] 复制失败(error " + std::to_wstring(err) + L"): " + srcPath + L" -> " + dstPath);
                // 不中断：继续复制其余文件，最大化本次更新落地的文件数。
            }
        }
    } while (FindNextFileW(hFind, &fd));

    FindClose(hFind);
    return overallOk;
}

// ---------------------------------------------------------------
// 取路径最末文件名段（用于从 --exe 推断进程名，仅日志用途）。
// ---------------------------------------------------------------
std::wstring FileName(const std::wstring& path) {
    std::wstring p = TrimTrailingSep(path);
    std::size_t pos = p.find_last_of(L"\\/");
    return pos == std::wstring::npos ? p : p.substr(pos + 1);
}

// ---------------------------------------------------------------
// 等待主程序进程退出。
//   - pid：主程序进程 ID。
// 流程：OpenProcess(SYNCHRONIZE) 拿句柄 -> WaitForSingleObject(超时 kWaitTimeoutMs)。
//        OpenProcess 失败通常意味进程已退出，直接继续；等待成功/超时后均额外 Sleep 缓冲。
// ---------------------------------------------------------------
void WaitForMainExit(DWORD pid) {
    if (pid == 0) {
        LogLine(L"[WAIT] 未提供有效 pid，跳过等待。");
    } else {
        // SYNCHRONIZE 权限足以对句柄做 WaitForSingleObject。
        HANDLE hProc = OpenProcess(SYNCHRONIZE, FALSE, pid);
        if (hProc == nullptr) {
            // 拿不到句柄：最可能是进程已退出（也可能权限不足），无论如何继续后续覆盖。
            LogLine(L"[WAIT] OpenProcess 失败(进程可能已退出, pid=" + std::to_wstring(pid) + L")，继续。");
        } else {
            DWORD waitResult = WaitForSingleObject(hProc, kWaitTimeoutMs);
            if (waitResult == WAIT_OBJECT_0) {
                LogLine(L"[WAIT] 主程序已退出。");
            } else if (waitResult == WAIT_TIMEOUT) {
                // 超过上限不再死等，避免 updater 永久卡住；后续复制靠重试兜底占用问题。
                LogLine(L"[WAIT] 等待主程序退出超时(" + std::to_wstring(kWaitTimeoutMs) + L"ms)，强行继续。");
            } else {
                LogLine(L"[WAIT] 等待返回异常(code=" + std::to_wstring(waitResult) + L")，继续。");
            }
            CloseHandle(hProc);
        }
    }
    // 额外缓冲：即便进程对象已 signaled，其文件句柄回收可能略有延迟。
    Sleep(kPostExitSleepMs);
}

// ---------------------------------------------------------------
// 重启主程序。用 CreateProcessW 启动 exePath，工作目录设为 target 安装目录。
//   - exePath：要启动的可执行完整路径（通常 target\DevSwitch.App.exe）。
//   - workingDir：新进程的工作目录（安装目录）。
// 返回是否成功拉起新进程。使用 CREATE_NO_WINDOW 避免闪出多余控制台窗口。
// ---------------------------------------------------------------
bool RestartMainApp(const std::wstring& exePath, const std::wstring& workingDir) {
    // CreateProcessW 的 lpCommandLine 需可写缓冲；首段填可执行路径并加引号（防路径含空格）。
    std::wstring commandLine = L"\"" + exePath + L"\"";
    std::vector<wchar_t> cmdBuffer(commandLine.begin(), commandLine.end());
    cmdBuffer.push_back(L'\0');

    STARTUPINFOW si{};
    si.cb = sizeof(si);

    PROCESS_INFORMATION pi{};
    BOOL ok = CreateProcessW(
        exePath.c_str(),                                   // lpApplicationName：显式指定可执行
        cmdBuffer.data(),                                  // lpCommandLine：可写命令行
        nullptr,
        nullptr,
        FALSE,                                             // 不继承句柄：updater 与新主程序无需共享
        CREATE_NO_WINDOW,                                  // 不弹控制台窗口（updater 无 GUI 需求）
        nullptr,                                           // 继承环境块
        workingDir.empty() ? nullptr : workingDir.c_str(), // 工作目录设为安装目录
        &si,
        &pi);

    if (!ok) {
        DWORD err = GetLastError();
        LogLine(L"[RESTART] 启动主程序失败(error " + std::to_wstring(err) + L"): " + exePath);
        return false;
    }
    // 拉起即可，无需等待主程序运行结束；及时关闭句柄避免泄漏。
    CloseHandle(pi.hThread);
    CloseHandle(pi.hProcess);
    LogLine(L"[RESTART] 已重启主程序: " + exePath);
    return true;
}

} // namespace

// wmain：-municode 下 Unicode 入口；但本程序按要求用 CommandLineToArgvW 自行解析，故忽略框架 argv。
int wmain() {
    // ---------- 1. 解析命令行参数 ----------
    // 用 CommandLineToArgvW 拆分原始命令行为宽字符 argv 数组（正确处理引号与空格）。
    int argc = 0;
    LPWSTR* argv = CommandLineToArgvW(GetCommandLineW(), &argc);
    if (argv == nullptr) {
        return kExitBadArgs;
    }

    std::wstring source;   // --source：新版解压目录
    std::wstring target;   // --target：当前安装目录
    std::wstring exePath;  // --exe   ：覆盖后要重启的 exe 完整路径
    DWORD pid = 0;         // --pid   ：主程序进程 ID
    bool hasPid = false;

    // 从 argv[1] 起按 "--key value" 成对解析；先把 --log 提取出来以便后续步骤即可写日志。
    for (int i = 1; i < argc; ++i) {
        std::wstring key = argv[i];
        // 所有已知开关都需要紧跟一个值。
        auto nextValue = [&](std::wstring& out) -> bool {
            if (i + 1 < argc) {
                out = argv[++i];
                return true;
            }
            return false;
        };
        if (key == L"--source") {
            nextValue(source);
        } else if (key == L"--target") {
            nextValue(target);
        } else if (key == L"--exe") {
            nextValue(exePath);
        } else if (key == L"--log") {
            std::wstring v;
            if (nextValue(v)) {
                g_logPath = v;  // 立即生效，后续所有 LogLine 都会落盘
            }
        } else if (key == L"--pid") {
            std::wstring v;
            if (nextValue(v)) {
                // 字符串转无符号整数；非法输入则 pid 维持 0（后续按“无有效 pid”处理）。
                try {
                    pid = static_cast<DWORD>(std::stoul(v));
                    hasPid = true;
                } catch (...) {
                    hasPid = false;
                }
            }
        }
        // 未知参数静默忽略，提升向前兼容性。
    }

    // argv 由 CommandLineToArgvW 分配，需 LocalFree 释放（值已拷入 std::wstring，可安全释放）。
    LocalFree(argv);

    LogLine(L"==================== DevSwitch.Updater 开始 ====================");
    LogLine(L"[ARGS] source=" + source);
    LogLine(L"[ARGS] target=" + target);
    LogLine(L"[ARGS] exe=" + exePath);
    LogLine(L"[ARGS] pid=" + (hasPid ? std::to_wstring(pid) : std::wstring(L"<none>")));

    // 必需参数校验：source / target / exe / pid 缺一不可。
    if (source.empty() || target.empty() || exePath.empty() || !hasPid) {
        LogLine(L"[ERROR] 缺少必需参数(--source/--target/--exe/--pid)，退出码 1。");
        return kExitBadArgs;
    }

    // 规整路径：去掉结尾分隔符，统一后续拼接行为。
    source = TrimTrailingSep(source);
    target = TrimTrailingSep(target);

    // 致命前置：source 不存在则无可复制，直接返回 3。
    if (!DirectoryExists(source)) {
        LogLine(L"[FATAL] source 目录不存在: " + source + L"，退出码 3。");
        return kExitFatal;
    }

    // ---------- 2. 等待主程序退出 ----------
    WaitForMainExit(pid);

    // ---------- 3. 覆盖复制 source -> target ----------
    int failedCount = 0;
    LogLine(L"[COPY] 开始覆盖复制: " + source + L" -> " + target);
    // 顶层调用：isTopLevel=true，selfName 为 updater 自身固定文件名。
    CopyTreeOverwrite(source, target, /*isTopLevel*/ true, L"DevSwitch.Updater.exe", failedCount);
    LogLine(L"[COPY] 复制结束，失败文件数=" + std::to_wstring(failedCount));

    // ---------- 4. 重启主程序 ----------
    bool restarted = RestartMainApp(exePath, target);

    // ---------- 5. 计算退出码 ----------
    int exitCode = kExitOk;
    if (failedCount > 0) {
        // 有文件失败：标记部分失败（即便重启成功也算 2，方便上层感知更新不完整）。
        exitCode = kExitPartialFail;
    }
    if (!restarted) {
        // 重启失败属严重问题：若此前没有更严重码（fatal 已提前返回），至少标记部分失败。
        if (exitCode == kExitOk) {
            exitCode = kExitPartialFail;
        }
    }
    LogLine(L"[DONE] 更新结束，退出码=" + std::to_wstring(exitCode) +
            L"（重启" + (restarted ? L"成功" : L"失败") + L"）");
    LogLine(L"==================== DevSwitch.Updater 结束 ====================");
    return exitCode;
}
