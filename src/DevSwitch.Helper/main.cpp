// 文件用途：DevSwitch 隐藏 helper 进程，提供 stdin/stdout JSON 协议与 Windows current SDK 链接操作。
// 创建/修改日期：2026-06-09
// 语言版本要求：C++20
// 依赖库：C++ 标准库、Win32 API
// NOTE: 合法授权学习使用，仅限本地环境。本 helper 只操作调用方传入的 current link 路径，不递归删除真实 SDK 目录。

#include <windows.h>
#include <winioctl.h>

#include <algorithm>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <cwchar>
#include <cwctype>
#include <iomanip>
#include <iostream>
#include <map>
#include <optional>
#include <sstream>
#include <string>
#include <string_view>
#include <vector>

#ifndef MAXIMUM_REPARSE_DATA_BUFFER_SIZE
#define MAXIMUM_REPARSE_DATA_BUFFER_SIZE (16 * 1024)
#endif

#ifndef SYMBOLIC_LINK_FLAG_ALLOW_UNPRIVILEGED_CREATE
#define SYMBOLIC_LINK_FLAG_ALLOW_UNPRIVILEGED_CREATE 0x2
#endif

namespace {

constexpr std::size_t MaxInputBytes = 64 * 1024;
constexpr int MaxJsonDepth = 32;

// MinGW 头文件在部分环境中不暴露 REPARSE_DATA_BUFFER，helper 只需要 mount point / symlink 子集。
typedef struct DevSwitchReparseDataBuffer {
    ULONG ReparseTag;
    USHORT ReparseDataLength;
    USHORT Reserved;
    union {
        struct {
            USHORT SubstituteNameOffset;
            USHORT SubstituteNameLength;
            USHORT PrintNameOffset;
            USHORT PrintNameLength;
            WCHAR PathBuffer[1];
        } MountPointReparseBuffer;
        struct {
            USHORT SubstituteNameOffset;
            USHORT SubstituteNameLength;
            USHORT PrintNameOffset;
            USHORT PrintNameLength;
            ULONG Flags;
            WCHAR PathBuffer[1];
        } SymbolicLinkReparseBuffer;
        struct {
            UCHAR DataBuffer[1];
        } GenericReparseBuffer;
    };
} DevSwitchReparseDataBuffer;

enum class JsonType {
    Null,
    Bool,
    Number,
    String,
    Object,
    Array,
};

struct JsonValue {
    JsonType type = JsonType::Null;
    bool boolValue = false;
    std::string stringValue;
    std::map<std::string, JsonValue> objectValue;
    std::vector<JsonValue> arrayValue;
};

struct HelperRequest {
    std::string requestId;
    std::string operation;
    JsonValue payload;
    bool hasPayload = false;
};

struct ParseRequestResult {
    bool ok = false;
    bool invalidJson = false;
    std::string requestId;
    std::string message;
    std::string detailsJson;
    HelperRequest request;
};

struct OperationResult {
    bool success = false;
    int exitCode = 3;
    std::string errorCode;
    std::string message;
    std::string detailsJson = "{}";
};

struct LinkInfo {
    bool exists = false;
    bool isDirectory = false;
    bool isReparsePoint = false;
    DWORD reparseTag = 0;
    std::string linkType = "missing";
    std::wstring targetPathW;
    std::string targetPath;
    std::string printName;
    DWORD win32Error = 0;
};

class JsonParser {
public:
    explicit JsonParser(std::string_view input) : input_(input) {}

    bool Parse(JsonValue& value) {
        SkipWhitespace();
        if (!ParseValue(value, 0)) {
            return false;
        }

        SkipWhitespace();
        return position_ == input_.size();
    }

private:
    bool ParseValue(JsonValue& value, int depth) {
        if (depth > MaxJsonDepth) {
            return false;
        }

        SkipWhitespace();
        if (position_ >= input_.size()) {
            return false;
        }

        char ch = input_[position_];
        if (ch == '"') {
            value.type = JsonType::String;
            return ParseString(value.stringValue);
        }

        if (ch == '{') {
            return ParseObject(value, depth + 1);
        }

        if (ch == '[') {
            return ParseArray(value, depth + 1);
        }

        if (StartsWith("true")) {
            position_ += 4;
            value.type = JsonType::Bool;
            value.boolValue = true;
            return true;
        }

        if (StartsWith("false")) {
            position_ += 5;
            value.type = JsonType::Bool;
            value.boolValue = false;
            return true;
        }

        if (StartsWith("null")) {
            position_ += 4;
            value.type = JsonType::Null;
            return true;
        }

        if (ch == '-' || (ch >= '0' && ch <= '9')) {
            return ParseNumber(value);
        }

        return false;
    }

    bool ParseObject(JsonValue& value, int depth) {
        value.type = JsonType::Object;
        value.objectValue.clear();
        ++position_; // 跳过 {
        SkipWhitespace();

        if (Consume('}')) {
            return true;
        }

        while (position_ < input_.size()) {
            std::string key;
            if (!ParseString(key)) {
                return false;
            }

            SkipWhitespace();
            if (!Consume(':')) {
                return false;
            }

            JsonValue propertyValue;
            if (!ParseValue(propertyValue, depth)) {
                return false;
            }

            value.objectValue[key] = std::move(propertyValue);
            SkipWhitespace();

            if (Consume('}')) {
                return true;
            }

            if (!Consume(',')) {
                return false;
            }

            SkipWhitespace();
        }

        return false;
    }

    bool ParseArray(JsonValue& value, int depth) {
        value.type = JsonType::Array;
        value.arrayValue.clear();
        ++position_; // 跳过 [
        SkipWhitespace();

        if (Consume(']')) {
            return true;
        }

        while (position_ < input_.size()) {
            JsonValue item;
            if (!ParseValue(item, depth)) {
                return false;
            }

            value.arrayValue.push_back(std::move(item));
            SkipWhitespace();

            if (Consume(']')) {
                return true;
            }

            if (!Consume(',')) {
                return false;
            }

            SkipWhitespace();
        }

        return false;
    }

    bool ParseString(std::string& value) {
        if (!Consume('"')) {
            return false;
        }

        std::ostringstream decoded;
        while (position_ < input_.size()) {
            unsigned char ch = static_cast<unsigned char>(input_[position_++]);
            if (ch == '"') {
                value = decoded.str();
                return true;
            }

            if (ch < 0x20) {
                return false;
            }

            if (ch != '\\') {
                decoded << static_cast<char>(ch);
                continue;
            }

            if (position_ >= input_.size()) {
                return false;
            }

            char escape = input_[position_++];
            switch (escape) {
            case '"': decoded << '"'; break;
            case '\\': decoded << '\\'; break;
            case '/': decoded << '/'; break;
            case 'b': decoded << '\b'; break;
            case 'f': decoded << '\f'; break;
            case 'n': decoded << '\n'; break;
            case 'r': decoded << '\r'; break;
            case 't': decoded << '\t'; break;
            case 'u': {
                uint32_t codePoint = 0;
                if (!ParseHex4(codePoint)) {
                    return false;
                }
                AppendUtf8(decoded, codePoint);
                break;
            }
            default:
                return false;
            }
        }

        return false;
    }

    bool ParseNumber(JsonValue& value) {
        const std::size_t start = position_;
        if (input_[position_] == '-') {
            ++position_;
        }

        if (position_ >= input_.size()) {
            return false;
        }

        if (input_[position_] == '0') {
            ++position_;
        }
        else if (input_[position_] >= '1' && input_[position_] <= '9') {
            while (position_ < input_.size() && input_[position_] >= '0' && input_[position_] <= '9') {
                ++position_;
            }
        }
        else {
            return false;
        }

        if (position_ < input_.size() && input_[position_] == '.') {
            ++position_;
            if (position_ >= input_.size() || input_[position_] < '0' || input_[position_] > '9') {
                return false;
            }
            while (position_ < input_.size() && input_[position_] >= '0' && input_[position_] <= '9') {
                ++position_;
            }
        }

        if (position_ < input_.size() && (input_[position_] == 'e' || input_[position_] == 'E')) {
            ++position_;
            if (position_ < input_.size() && (input_[position_] == '+' || input_[position_] == '-')) {
                ++position_;
            }
            if (position_ >= input_.size() || input_[position_] < '0' || input_[position_] > '9') {
                return false;
            }
            while (position_ < input_.size() && input_[position_] >= '0' && input_[position_] <= '9') {
                ++position_;
            }
        }

        value.type = JsonType::Number;
        value.stringValue = std::string(input_.substr(start, position_ - start));
        return true;
    }

    bool ParseHex4(uint32_t& codePoint) {
        if (position_ + 4 > input_.size()) {
            return false;
        }

        codePoint = 0;
        for (int i = 0; i < 4; ++i) {
            char ch = input_[position_++];
            codePoint <<= 4;
            if (ch >= '0' && ch <= '9') {
                codePoint += static_cast<uint32_t>(ch - '0');
            }
            else if (ch >= 'a' && ch <= 'f') {
                codePoint += static_cast<uint32_t>(10 + ch - 'a');
            }
            else if (ch >= 'A' && ch <= 'F') {
                codePoint += static_cast<uint32_t>(10 + ch - 'A');
            }
            else {
                return false;
            }
        }

        return true;
    }

    static void AppendUtf8(std::ostringstream& output, uint32_t codePoint) {
        // NOTE: helper 协议只需要稳定处理常见 BMP 字符；非法 surrogate 按替换符输出，避免崩溃。
        if (codePoint >= 0xD800 && codePoint <= 0xDFFF) {
            codePoint = 0xFFFD;
        }

        if (codePoint <= 0x7F) {
            output << static_cast<char>(codePoint);
        }
        else if (codePoint <= 0x7FF) {
            output << static_cast<char>(0xC0 | (codePoint >> 6));
            output << static_cast<char>(0x80 | (codePoint & 0x3F));
        }
        else {
            output << static_cast<char>(0xE0 | (codePoint >> 12));
            output << static_cast<char>(0x80 | ((codePoint >> 6) & 0x3F));
            output << static_cast<char>(0x80 | (codePoint & 0x3F));
        }
    }

    bool Consume(char expected) {
        if (position_ < input_.size() && input_[position_] == expected) {
            ++position_;
            return true;
        }

        return false;
    }

    bool StartsWith(std::string_view value) const {
        return input_.substr(position_, value.size()) == value;
    }

    void SkipWhitespace() {
        while (position_ < input_.size()) {
            char ch = input_[position_];
            if (ch == ' ' || ch == '\t' || ch == '\r' || ch == '\n') {
                ++position_;
            }
            else {
                break;
            }
        }
    }

    std::string_view input_;
    std::size_t position_ = 0;
};

std::string ReadAllStdin() {
    std::ostringstream buffer;
    buffer << std::cin.rdbuf();
    return buffer.str();
}

std::string EscapeJsonString(const std::string& value) {
    std::ostringstream escaped;

    for (unsigned char ch : value) {
        switch (ch) {
        case '\\': escaped << "\\\\"; break;
        case '"': escaped << "\\\""; break;
        case '\n': escaped << "\\n"; break;
        case '\r': escaped << "\\r"; break;
        case '\t': escaped << "\\t"; break;
        case '\b': escaped << "\\b"; break;
        case '\f': escaped << "\\f"; break;
        default:
            if (ch < 0x20) {
                escaped << "\\u" << std::hex << std::setw(4) << std::setfill('0') << static_cast<int>(ch) << std::dec;
            }
            else {
                escaped << static_cast<char>(ch);
            }
            break;
        }
    }

    return escaped.str();
}

std::string JsonString(const std::string& value) {
    return "\"" + EscapeJsonString(value) + "\"";
}

std::string JsonNullableString(const std::string& value) {
    return value.empty() ? "null" : JsonString(value);
}

std::string BoolJson(bool value) {
    return value ? "true" : "false";
}

std::string BuildResponseJson(const std::string& requestId, bool success, const std::string& errorCode, const std::string& message, const std::string& detailsJson = "{}") {
    std::ostringstream response;
    response << "{";
    response << "\"requestId\":" << JsonString(requestId) << ",";
    response << "\"success\":" << BoolJson(success) << ",";
    response << "\"errorCode\":" << (errorCode.empty() ? "null" : JsonString(errorCode)) << ",";
    response << "\"message\":" << JsonString(message) << ",";
    response << "\"details\":" << (detailsJson.empty() ? "{}" : detailsJson);
    response << "}";
    return response.str();
}

const JsonValue* FindProperty(const JsonValue& object, const std::string& name) {
    if (object.type != JsonType::Object) {
        return nullptr;
    }

    auto iterator = object.objectValue.find(name);
    if (iterator == object.objectValue.end()) {
        return nullptr;
    }

    return &iterator->second;
}

std::optional<std::string> GetStringProperty(const JsonValue& object, const std::string& name) {
    const JsonValue* value = FindProperty(object, name);
    if (value == nullptr || value->type != JsonType::String) {
        return std::nullopt;
    }

    return value->stringValue;
}

ParseRequestResult ParseRequest(const std::string& input) {
    ParseRequestResult result;
    if (input.size() > MaxInputBytes) {
        result.invalidJson = true;
        result.message = "request too large";
        result.detailsJson = "{\"reason\":\"request-too-large\"}";
        return result;
    }

    JsonValue root;
    JsonParser parser(input);
    if (!parser.Parse(root) || root.type != JsonType::Object) {
        result.invalidJson = true;
        result.message = "invalid JSON";
        result.detailsJson = "{\"reason\":\"parse-error\"}";
        return result;
    }

    std::vector<std::string> missing;
    auto requestId = GetStringProperty(root, "requestId");
    auto operation = GetStringProperty(root, "operation");

    if (!requestId.has_value()) {
        missing.push_back("requestId");
    }
    else {
        result.requestId = requestId.value();
    }

    if (!operation.has_value()) {
        missing.push_back("operation");
    }

    if (!missing.empty()) {
        std::ostringstream details;
        details << "{\"missing\":[";
        for (std::size_t index = 0; index < missing.size(); ++index) {
            if (index != 0) {
                details << ",";
            }
            details << JsonString(missing[index]);
        }
        details << "]}";

        result.message = missing.size() == 1 ? "missing " + missing.front() : "missing required fields";
        result.detailsJson = details.str();
        return result;
    }

    result.ok = true;
    result.request.requestId = requestId.value();
    result.request.operation = operation.value();

    const JsonValue* payload = FindProperty(root, "payload");
    if (payload != nullptr) {
        result.request.payload = *payload;
        result.request.hasPayload = true;
    }

    return result;
}

std::wstring Utf8ToWide(const std::string& value) {
    if (value.empty()) {
        return std::wstring();
    }

    int length = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, value.data(), static_cast<int>(value.size()), nullptr, 0);
    if (length == 0) {
        length = MultiByteToWideChar(CP_ACP, 0, value.data(), static_cast<int>(value.size()), nullptr, 0);
        if (length == 0) {
            return std::wstring(value.begin(), value.end());
        }

        std::wstring wide(static_cast<std::size_t>(length), L'\0');
        MultiByteToWideChar(CP_ACP, 0, value.data(), static_cast<int>(value.size()), wide.data(), length);
        return wide;
    }

    std::wstring wide(static_cast<std::size_t>(length), L'\0');
    MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, value.data(), static_cast<int>(value.size()), wide.data(), length);
    return wide;
}

std::string WideToUtf8(const std::wstring& value) {
    if (value.empty()) {
        return std::string();
    }

    int length = WideCharToMultiByte(CP_UTF8, 0, value.data(), static_cast<int>(value.size()), nullptr, 0, nullptr, nullptr);
    if (length == 0) {
        return std::string(value.begin(), value.end());
    }

    std::string utf8(static_cast<std::size_t>(length), '\0');
    WideCharToMultiByte(CP_UTF8, 0, value.data(), static_cast<int>(value.size()), utf8.data(), length, nullptr, nullptr);
    return utf8;
}

std::wstring StripNtPathPrefix(std::wstring value) {
    constexpr std::wstring_view ntPrefix = L"\\??\\";
    constexpr std::wstring_view longPrefix = L"\\\\?\\";

    if (value.rfind(ntPrefix, 0) == 0) {
        value.erase(0, ntPrefix.size());
    }
    else if (value.rfind(longPrefix, 0) == 0) {
        value.erase(0, longPrefix.size());
    }

    return value;
}

std::wstring AddNtPathPrefix(std::wstring value) {
    if (value.rfind(L"\\??\\", 0) == 0) {
        return value;
    }

    if (value.rfind(L"\\\\?\\", 0) == 0) {
        value.erase(0, 4);
    }

    return L"\\??\\" + value;
}

std::wstring GetFullPathSafe(const std::wstring& path) {
    DWORD required = GetFullPathNameW(path.c_str(), 0, nullptr, nullptr);
    if (required == 0) {
        return path;
    }

    std::wstring buffer(static_cast<std::size_t>(required), L'\0');
    DWORD written = GetFullPathNameW(path.c_str(), required, buffer.data(), nullptr);
    if (written == 0) {
        return path;
    }

    buffer.resize(written);
    while (buffer.size() > 3 && (buffer.back() == L'\\' || buffer.back() == L'/')) {
        buffer.pop_back();
    }

    return buffer;
}

bool PathsEqual(const std::wstring& left, const std::wstring& right) {
    std::wstring normalizedLeft = GetFullPathSafe(StripNtPathPrefix(left));
    std::wstring normalizedRight = GetFullPathSafe(StripNtPathPrefix(right));
    return _wcsicmp(normalizedLeft.c_str(), normalizedRight.c_str()) == 0;
}

std::string FormatReparseTag(DWORD tag) {
    std::ostringstream builder;
    builder << "0x" << std::uppercase << std::hex << tag;
    return builder.str();
}

std::string Win32Details(DWORD error) {
    std::ostringstream details;
    details << "{\"win32Error\":" << error << "}";
    return details.str();
}

std::string MapWin32Error(DWORD error, const std::string& fallback = "win32-error") {
    switch (error) {
    case ERROR_ACCESS_DENIED:
        return "access-denied";
    case ERROR_PRIVILEGE_NOT_HELD:
        return "privilege-not-held";
    case ERROR_SHARING_VIOLATION:
        return "sharing-violation";
    case ERROR_FILE_NOT_FOUND:
    case ERROR_PATH_NOT_FOUND:
        return "path-not-found";
    default:
        return fallback;
    }
}

bool IsMissingError(DWORD error) {
    return error == ERROR_FILE_NOT_FOUND || error == ERROR_PATH_NOT_FOUND || error == ERROR_NOT_FOUND;
}

bool IsDirectoryReparseLink(const LinkInfo& info) {
    return info.exists && info.isDirectory && (info.linkType == "junction" || info.linkType == "directory-symlink");
}

std::string LinkInfoDetails(const LinkInfo& info, const std::string& pathUtf8, const std::string& extraJson = "") {
    std::ostringstream details;
    details << "{";
    details << "\"exists\":" << BoolJson(info.exists) << ",";
    details << "\"path\":" << JsonString(pathUtf8) << ",";
    details << "\"linkType\":" << JsonString(info.linkType) << ",";
    details << "\"isDirectory\":" << BoolJson(info.isDirectory) << ",";
    details << "\"isReparsePoint\":" << BoolJson(info.isReparsePoint) << ",";
    details << "\"reparseTag\":" << (info.reparseTag == 0 ? "null" : JsonString(FormatReparseTag(info.reparseTag))) << ",";
    details << "\"targetPath\":" << JsonNullableString(info.targetPath) << ",";
    details << "\"printName\":" << JsonNullableString(info.printName);
    if (!extraJson.empty()) {
        details << "," << extraJson;
    }
    details << "}";
    return details.str();
}

LinkInfo InspectLinkPath(const std::wstring& pathW) {
    LinkInfo info;
    DWORD attributes = GetFileAttributesW(pathW.c_str());
    if (attributes == INVALID_FILE_ATTRIBUTES) {
        DWORD error = GetLastError();
        if (IsMissingError(error)) {
            return info;
        }

        info.win32Error = error;
        info.linkType = "error";
        return info;
    }

    info.exists = true;
    info.isDirectory = (attributes & FILE_ATTRIBUTE_DIRECTORY) != 0;
    info.isReparsePoint = (attributes & FILE_ATTRIBUTE_REPARSE_POINT) != 0;

    if (!info.isReparsePoint) {
        info.linkType = info.isDirectory ? "real-directory" : "real-file";
        return info;
    }

    HANDLE handle = CreateFileW(
        pathW.c_str(),
        GENERIC_READ,
        FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
        nullptr,
        OPEN_EXISTING,
        FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAG_BACKUP_SEMANTICS,
        nullptr);

    if (handle == INVALID_HANDLE_VALUE) {
        info.win32Error = GetLastError();
        info.linkType = "error";
        return info;
    }

    std::vector<BYTE> buffer(MAXIMUM_REPARSE_DATA_BUFFER_SIZE);
    DWORD bytesReturned = 0;
    BOOL ok = DeviceIoControl(
        handle,
        FSCTL_GET_REPARSE_POINT,
        nullptr,
        0,
        buffer.data(),
        static_cast<DWORD>(buffer.size()),
        &bytesReturned,
        nullptr);
    CloseHandle(handle);

    if (!ok) {
        info.win32Error = GetLastError();
        info.linkType = "error";
        return info;
    }

    auto* reparse = reinterpret_cast<DevSwitchReparseDataBuffer*>(buffer.data());
    info.reparseTag = reparse->ReparseTag;

    if (reparse->ReparseTag == IO_REPARSE_TAG_MOUNT_POINT) {
        auto& mount = reparse->MountPointReparseBuffer;
        std::wstring substitute(mount.PathBuffer + (mount.SubstituteNameOffset / sizeof(WCHAR)), mount.SubstituteNameLength / sizeof(WCHAR));
        std::wstring printName(mount.PathBuffer + (mount.PrintNameOffset / sizeof(WCHAR)), mount.PrintNameLength / sizeof(WCHAR));
        info.targetPathW = StripNtPathPrefix(substitute);
        info.targetPath = WideToUtf8(info.targetPathW);
        info.printName = WideToUtf8(printName.empty() ? info.targetPathW : printName);
        info.linkType = "junction";
        return info;
    }

    if (reparse->ReparseTag == IO_REPARSE_TAG_SYMLINK) {
        auto& symlink = reparse->SymbolicLinkReparseBuffer;
        std::wstring substitute(symlink.PathBuffer + (symlink.SubstituteNameOffset / sizeof(WCHAR)), symlink.SubstituteNameLength / sizeof(WCHAR));
        std::wstring printName(symlink.PathBuffer + (symlink.PrintNameOffset / sizeof(WCHAR)), symlink.PrintNameLength / sizeof(WCHAR));
        info.targetPathW = StripNtPathPrefix(substitute);
        info.targetPath = WideToUtf8(info.targetPathW);
        info.printName = WideToUtf8(printName.empty() ? info.targetPathW : printName);
        info.linkType = info.isDirectory ? "directory-symlink" : "file-symlink";
        return info;
    }

    info.linkType = "other-reparse";
    return info;
}

bool IsPathSeparator(wchar_t ch) {
    return ch == L'\\' || ch == L'/';
}

std::wstring TrimTrailingSeparators(std::wstring path) {
    while (path.size() > 3 && IsPathSeparator(path.back())) {
        path.pop_back();
    }
    return path;
}

std::wstring GetParentPath(std::wstring path) {
    path = TrimTrailingSeparators(std::move(path));
    std::size_t position = path.find_last_of(L"\\/");
    if (position == std::wstring::npos) {
        return L"";
    }

    if (position == 2 && path.size() > 2 && path[1] == L':') {
        return path.substr(0, 3);
    }

    if (position == 0) {
        return path.substr(0, 1);
    }

    return path.substr(0, position);
}

std::wstring GetFileName(std::wstring path) {
    path = TrimTrailingSeparators(std::move(path));
    std::size_t position = path.find_last_of(L"\\/");
    if (position == std::wstring::npos) {
        return path;
    }

    return path.substr(position + 1);
}

std::wstring CombinePath(const std::wstring& parent, const std::wstring& child) {
    if (parent.empty()) {
        return child;
    }

    if (IsPathSeparator(parent.back())) {
        return parent + child;
    }

    return parent + L"\\" + child;
}

bool DirectoryExists(const std::wstring& path) {
    DWORD attributes = GetFileAttributesW(path.c_str());
    return attributes != INVALID_FILE_ATTRIBUTES && (attributes & FILE_ATTRIBUTE_DIRECTORY) != 0;
}

// 判断目录是否为空（仅含 "." / ".."）。
//   - 若枚举失败（权限/不存在等），保守返回 false，避免在不能确认时误删。
//   - 仅枚举一层，不递归；空目录的判断对 RemoveDirectoryW 成立的前提是「无任何条目」。
// 用于 SwitchSdkCore / CreateCurrentLinkOperation 自愈被实体化的空 current 目录：
//   有的发布包/旧版本残留会把 current\<type> 留成普通空目录，使切换被拒。
//   只清理空目录足以解决；非空真实目录绝不触碰，保护用户可能误放进去的真实数据。
bool IsDirectoryEmpty(const std::wstring& pathW) {
    std::wstring pattern = CombinePath(pathW, L"*");
    WIN32_FIND_DATAW fd{};
    HANDLE h = FindFirstFileW(pattern.c_str(), &fd);
    if (h == INVALID_HANDLE_VALUE) {
        return false;
    }

    bool empty = true;
    do {
        const wchar_t* name = fd.cFileName;
        if (name[0] == L'.' && (name[1] == L'\0' || (name[1] == L'.' && name[2] == L'\0'))) {
            continue;
        }
        empty = false;
        break;
    } while (FindNextFileW(h, &fd));

    FindClose(h);
    return empty;
}

// 若 currentPath 是「空的真实目录」，删除它并返回 true，让调用方按「不存在」继续走创建链接流程。
// 非真实目录、非空真实目录、删除失败时一律返回 false，由调用方维持原有拒绝路径。
// 设计要点：
//   1) 只对 linkType == "real-directory" 触发；junction/symlink 等 reparse 入口不在此自愈。
//   2) RemoveDirectoryW 自身只能删空目录，对非空目录返回 ERROR_DIR_NOT_EMPTY，是第二层保险。
//   3) 自愈不递归、不改名备份，绝不在用户目录下做扩展行为。
bool TryHealEmptyRealDirectory(const std::wstring& pathW, const LinkInfo& info) {
    if (info.linkType != "real-directory") {
        return false;
    }
    if (!IsDirectoryEmpty(pathW)) {
        return false;
    }
    return RemoveDirectoryW(pathW.c_str()) != 0;
}

bool EnsureDirectoryRecursive(const std::wstring& directory, DWORD& error) {
    if (directory.empty()) {
        error = ERROR_PATH_NOT_FOUND;
        return false;
    }

    if (DirectoryExists(directory)) {
        error = 0;
        return true;
    }

    std::wstring parent = GetParentPath(directory);
    if (!parent.empty() && parent != directory && !DirectoryExists(parent)) {
        if (!EnsureDirectoryRecursive(parent, error)) {
            return false;
        }
    }

    if (CreateDirectoryW(directory.c_str(), nullptr) || GetLastError() == ERROR_ALREADY_EXISTS) {
        error = 0;
        return DirectoryExists(directory);
    }

    error = GetLastError();
    return false;
}

bool TargetDirectoryExists(const std::wstring& targetPathW, DWORD& error) {
    DWORD attributes = GetFileAttributesW(targetPathW.c_str());
    if (attributes == INVALID_FILE_ATTRIBUTES) {
        error = GetLastError();
        return false;
    }

    if ((attributes & FILE_ATTRIBUTE_DIRECTORY) == 0) {
        error = ERROR_DIRECTORY;
        return false;
    }

    error = 0;
    return true;
}

bool EnsureParentDirectory(const std::wstring& pathW, DWORD& error) {
    std::wstring parent = GetParentPath(pathW);
    if (parent.empty()) {
        error = ERROR_PATH_NOT_FOUND;
        return false;
    }

    return EnsureDirectoryRecursive(parent, error);
}

bool RemoveDirectoryLinkOnly(const std::wstring& pathW, LinkInfo* removedInfo, DWORD& error) {
    LinkInfo info = InspectLinkPath(pathW);
    if (removedInfo != nullptr) {
        *removedInfo = info;
    }

    if (!info.exists) {
        error = 0;
        return true;
    }

    if (!IsDirectoryReparseLink(info)) {
        error = ERROR_ACCESS_DENIED;
        return false;
    }

    if (!RemoveDirectoryW(pathW.c_str())) {
        error = GetLastError();
        return false;
    }

    error = 0;
    return true;
}

OperationResult CreateJunctionRaw(const std::wstring& currentPathW, const std::wstring& targetPathW) {
    DWORD targetError = 0;
    if (!TargetDirectoryExists(targetPathW, targetError)) {
        return OperationResult{ false, 3, IsMissingError(targetError) ? "target-not-found" : "target-not-directory", "target directory is not usable", Win32Details(targetError) };
    }

    DWORD parentError = 0;
    if (!EnsureParentDirectory(currentPathW, parentError)) {
        return OperationResult{ false, 3, "current-parent-not-found", "current parent directory is not usable", Win32Details(parentError) };
    }

    if (!CreateDirectoryW(currentPathW.c_str(), nullptr)) {
        DWORD error = GetLastError();
        return OperationResult{ false, 3, MapWin32Error(error, "create-current-directory-failed"), "failed to create current link placeholder", Win32Details(error) };
    }

    HANDLE handle = CreateFileW(
        currentPathW.c_str(),
        GENERIC_WRITE,
        0,
        nullptr,
        OPEN_EXISTING,
        FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAG_BACKUP_SEMANTICS,
        nullptr);

    if (handle == INVALID_HANDLE_VALUE) {
        DWORD error = GetLastError();
        RemoveDirectoryW(currentPathW.c_str());
        return OperationResult{ false, 3, MapWin32Error(error, "open-current-link-failed"), "failed to open current link placeholder", Win32Details(error) };
    }

    std::wstring fullTarget = GetFullPathSafe(targetPathW);
    std::wstring substituteName = AddNtPathPrefix(fullTarget);
    std::wstring printName = fullTarget;
    const USHORT substituteBytes = static_cast<USHORT>(substituteName.size() * sizeof(WCHAR));
    const USHORT printBytes = static_cast<USHORT>(printName.size() * sizeof(WCHAR));
    const USHORT pathBytes = static_cast<USHORT>(substituteBytes + sizeof(WCHAR) + printBytes + sizeof(WCHAR));
    const DWORD bufferSize = static_cast<DWORD>(offsetof(DevSwitchReparseDataBuffer, MountPointReparseBuffer.PathBuffer) + pathBytes);
    std::vector<BYTE> buffer(bufferSize, 0);

    auto* reparse = reinterpret_cast<DevSwitchReparseDataBuffer*>(buffer.data());
    reparse->ReparseTag = IO_REPARSE_TAG_MOUNT_POINT;
    reparse->ReparseDataLength = static_cast<USHORT>(sizeof(USHORT) * 4 + pathBytes);
    reparse->Reserved = 0;
    auto& mount = reparse->MountPointReparseBuffer;
    mount.SubstituteNameOffset = 0;
    mount.SubstituteNameLength = substituteBytes;
    mount.PrintNameOffset = static_cast<USHORT>(substituteBytes + sizeof(WCHAR));
    mount.PrintNameLength = printBytes;

    std::memcpy(mount.PathBuffer, substituteName.data(), substituteBytes);
    std::memcpy(reinterpret_cast<BYTE*>(mount.PathBuffer) + mount.PrintNameOffset, printName.data(), printBytes);

    DWORD bytesReturned = 0;
    BOOL ok = DeviceIoControl(
        handle,
        FSCTL_SET_REPARSE_POINT,
        buffer.data(),
        bufferSize,
        nullptr,
        0,
        &bytesReturned,
        nullptr);
    DWORD error = ok ? 0 : GetLastError();
    CloseHandle(handle);

    if (!ok) {
        RemoveDirectoryW(currentPathW.c_str());
        return OperationResult{ false, 3, MapWin32Error(error, "create-junction-failed"), "failed to create junction", Win32Details(error) };
    }

    return OperationResult{ true, 0, "", "current link created", "{}" };
}

OperationResult InspectLinkOperation(const HelperRequest& request) {
    auto currentPath = GetStringProperty(request.payload, "currentPath");
    if (!currentPath.has_value() || currentPath->empty()) {
        return OperationResult{ false, 1, "missing-current-path", "missing currentPath", "{\"missing\":[\"currentPath\"]}" };
    }

    LinkInfo info = InspectLinkPath(Utf8ToWide(*currentPath));
    if (info.linkType == "error") {
        return OperationResult{ false, 3, MapWin32Error(info.win32Error), "failed to inspect current link", Win32Details(info.win32Error) };
    }

    return OperationResult{ true, 0, "", "link inspected", LinkInfoDetails(info, *currentPath) };
}

OperationResult CreateCurrentLinkOperation(const HelperRequest& request) {
    auto currentPath = GetStringProperty(request.payload, "currentPath");
    auto targetPath = GetStringProperty(request.payload, "targetPath");
    if (!currentPath.has_value() || currentPath->empty()) {
        return OperationResult{ false, 1, "missing-current-path", "missing currentPath", "{\"missing\":[\"currentPath\"]}" };
    }
    if (!targetPath.has_value() || targetPath->empty()) {
        return OperationResult{ false, 1, "missing-target-path", "missing targetPath", "{\"missing\":[\"targetPath\"]}" };
    }

    std::wstring currentPathW = Utf8ToWide(*currentPath);
    std::wstring targetPathW = Utf8ToWide(*targetPath);
    LinkInfo existing = InspectLinkPath(currentPathW);
    if (existing.exists) {
        // 自愈：若是空的真实目录（常见为旧版本/发布包残留的占位目录），删除后按「不存在」继续创建链接。
        // 非空真实目录、真实文件、其他 reparse 入口仍维持原有拒绝路径，绝不动用户数据。
        if (existing.linkType == "real-directory") {
            if (TryHealEmptyRealDirectory(currentPathW, existing)) {
                existing = LinkInfo{};
            }
            else {
                return OperationResult{ false, 3, "unsafe-existing-directory", "current path is a real directory", LinkInfoDetails(existing, *currentPath) };
            }
        }
        else if (existing.linkType == "real-file") {
            return OperationResult{ false, 3, "unsafe-existing-file", "current path is a real file", LinkInfoDetails(existing, *currentPath) };
        }
        else {
            return OperationResult{ false, 3, "unsafe-reparse-point", "current path already exists", LinkInfoDetails(existing, *currentPath) };
        }
    }

    OperationResult created = CreateJunctionRaw(currentPathW, targetPathW);
    if (!created.success) {
        return created;
    }

    LinkInfo finalInfo = InspectLinkPath(currentPathW);
    if (!IsDirectoryReparseLink(finalInfo) || !PathsEqual(finalInfo.targetPathW, targetPathW)) {
        DWORD removeError = 0;
        RemoveDirectoryLinkOnly(currentPathW, nullptr, removeError);
        return OperationResult{ false, 3, "verify-created-link-failed", "created link did not point to target", LinkInfoDetails(finalInfo, *currentPath) };
    }

    std::ostringstream details;
    details << "{";
    details << "\"currentPath\":" << JsonString(*currentPath) << ",";
    details << "\"targetPath\":" << JsonString(WideToUtf8(GetFullPathSafe(targetPathW))) << ",";
    details << "\"linkType\":\"junction\",";
    details << "\"created\":true,";
    details << "\"replaced\":false";
    details << "}";
    return OperationResult{ true, 0, "", "current link created", details.str() };
}

OperationResult RemoveCurrentLinkOperation(const HelperRequest& request) {
    auto currentPath = GetStringProperty(request.payload, "currentPath");
    if (!currentPath.has_value() || currentPath->empty()) {
        return OperationResult{ false, 1, "missing-current-path", "missing currentPath", "{\"missing\":[\"currentPath\"]}" };
    }

    std::wstring currentPathW = Utf8ToWide(*currentPath);
    LinkInfo info = InspectLinkPath(currentPathW);
    if (!info.exists) {
        return OperationResult{ true, 0, "", "current link missing", "{\"currentPath\":" + JsonString(*currentPath) + ",\"removed\":false,\"linkType\":\"missing\"}" };
    }

    if (info.linkType == "real-directory") {
        return OperationResult{ false, 3, "unsafe-real-directory", "refusing to remove real directory", LinkInfoDetails(info, *currentPath) };
    }
    if (info.linkType == "real-file") {
        return OperationResult{ false, 3, "unsafe-existing-file", "refusing to remove real file", LinkInfoDetails(info, *currentPath) };
    }
    if (!IsDirectoryReparseLink(info)) {
        return OperationResult{ false, 3, "unsupported-reparse-point", "refusing to remove unsupported reparse point", LinkInfoDetails(info, *currentPath) };
    }

    // NOTE: 二次确认后才 RemoveDirectoryW，且只删除链接入口本身，不递归、不遍历目标目录。
    LinkInfo confirm = InspectLinkPath(currentPathW);
    if (!IsDirectoryReparseLink(confirm)) {
        return OperationResult{ false, 3, "unsafe-real-directory", "current path changed before removal", LinkInfoDetails(confirm, *currentPath) };
    }

    if (!RemoveDirectoryW(currentPathW.c_str())) {
        DWORD error = GetLastError();
        return OperationResult{ false, 3, MapWin32Error(error, "remove-link-failed"), "failed to remove current link", Win32Details(error) };
    }

    std::ostringstream details;
    details << "{";
    details << "\"currentPath\":" << JsonString(*currentPath) << ",";
    details << "\"removed\":true,";
    details << "\"linkType\":" << JsonString(info.linkType) << ",";
    details << "\"targetPath\":" << JsonNullableString(info.targetPath);
    details << "}";
    return OperationResult{ true, 0, "", "current link removed", details.str() };
}

bool MovePath(const std::wstring& from, const std::wstring& to, DWORD& error) {
    if (MoveFileExW(from.c_str(), to.c_str(), MOVEFILE_WRITE_THROUGH)) {
        error = 0;
        return true;
    }

    error = GetLastError();
    return false;
}

OperationResult SwitchSdkCore(const std::wstring& currentPathW, const std::wstring& targetPathW, const std::string& currentPathUtf8, const std::string& requestId) {
    DWORD targetError = 0;
    if (!TargetDirectoryExists(targetPathW, targetError)) {
        return OperationResult{ false, 3, IsMissingError(targetError) ? "target-path-missing" : "target-path-not-directory", "target path is not usable", Win32Details(targetError) };
    }

    DWORD parentError = 0;
    if (!EnsureParentDirectory(currentPathW, parentError)) {
        return OperationResult{ false, 3, "current-parent-not-found", "current parent directory is not usable", Win32Details(parentError) };
    }

    LinkInfo before = InspectLinkPath(currentPathW);
    if (before.linkType == "real-directory") {
        // 自愈：空的真实目录（如旧版本残留的占位）删除后按「不存在」继续。
        // 非空真实目录维持拒绝，避免误删用户实际数据。
        if (TryHealEmptyRealDirectory(currentPathW, before)) {
            before = LinkInfo{};
        }
        else {
            return OperationResult{ false, 3, "current-path-not-managed-link", "current path is a real directory", LinkInfoDetails(before, currentPathUtf8) };
        }
    }
    if (before.linkType == "real-file") {
        return OperationResult{ false, 3, "current-path-is-file", "current path is a real file", LinkInfoDetails(before, currentPathUtf8) };
    }
    if (before.exists && !IsDirectoryReparseLink(before)) {
        return OperationResult{ false, 3, "current-path-not-managed-link", "current path is not a managed link", LinkInfoDetails(before, currentPathUtf8) };
    }

    std::wstring parentW = GetParentPath(currentPathW);
    std::wstring nameW = GetFileName(currentPathW);
    std::wstring safeRequestId = Utf8ToWide(requestId.empty() ? "request" : requestId);
    for (wchar_t& ch : safeRequestId) {
        if (!(ch >= L'0' && ch <= L'9') && !(ch >= L'a' && ch <= L'z') && !(ch >= L'A' && ch <= L'Z')) {
            ch = L'_';
        }
    }

    std::wstring tempPathW = CombinePath(parentW, L"." + nameW + L".new." + safeRequestId);
    std::wstring backupPathW = CombinePath(parentW, L"." + nameW + L".old." + safeRequestId);

    // NOTE: 清理同 requestId 残留时仍只删除 reparse link，不碰普通目录。
    DWORD cleanupError = 0;
    RemoveDirectoryLinkOnly(tempPathW, nullptr, cleanupError);
    RemoveDirectoryLinkOnly(backupPathW, nullptr, cleanupError);

    OperationResult createTemp = CreateJunctionRaw(tempPathW, targetPathW);
    if (!createTemp.success) {
        createTemp.errorCode = "create-temp-link-failed";
        createTemp.message = "failed to create temporary current link";
        return createTemp;
    }

    LinkInfo tempInfo = InspectLinkPath(tempPathW);
    if (!IsDirectoryReparseLink(tempInfo) || !PathsEqual(tempInfo.targetPathW, targetPathW)) {
        RemoveDirectoryLinkOnly(tempPathW, nullptr, cleanupError);
        return OperationResult{ false, 3, "verify-temp-link-failed", "temporary link did not point to target", LinkInfoDetails(tempInfo, WideToUtf8(tempPathW)) };
    }

    bool currentMovedToBackup = false;
    bool tempMovedToCurrent = false;
    DWORD moveError = 0;

    if (before.exists) {
        if (!MovePath(currentPathW, backupPathW, moveError)) {
            RemoveDirectoryLinkOnly(tempPathW, nullptr, cleanupError);
            return OperationResult{ false, 3, "rename-current-to-backup-failed", "failed to backup current link", Win32Details(moveError) };
        }
        currentMovedToBackup = true;
    }

    if (!MovePath(tempPathW, currentPathW, moveError)) {
        if (currentMovedToBackup) {
            DWORD rollbackMoveError = 0;
            MovePath(backupPathW, currentPathW, rollbackMoveError);
        }
        RemoveDirectoryLinkOnly(tempPathW, nullptr, cleanupError);
        return OperationResult{ false, 3, "rename-temp-to-current-failed", "failed to replace current link", Win32Details(moveError) };
    }
    tempMovedToCurrent = true;

    LinkInfo finalInfo = InspectLinkPath(currentPathW);
    if (!IsDirectoryReparseLink(finalInfo) || !PathsEqual(finalInfo.targetPathW, targetPathW)) {
        bool rollbackSucceeded = true;
        if (tempMovedToCurrent) {
            DWORD removeNewError = 0;
            rollbackSucceeded = RemoveDirectoryLinkOnly(currentPathW, nullptr, removeNewError) && rollbackSucceeded;
        }
        if (currentMovedToBackup) {
            DWORD rollbackMoveError = 0;
            rollbackSucceeded = MovePath(backupPathW, currentPathW, rollbackMoveError) && rollbackSucceeded;
        }

        std::ostringstream details;
        details << LinkInfoDetails(finalInfo, currentPathUtf8, "\"rollbackAttempted\":true,\"rollbackSucceeded\":" + BoolJson(rollbackSucceeded));
        return OperationResult{ false, rollbackSucceeded ? 3 : 4, rollbackSucceeded ? "verify-final-link-failed" : "rollback-failed", "final current link verification failed", details.str() };
    }

    if (currentMovedToBackup) {
        DWORD removeBackupError = 0;
        if (!RemoveDirectoryLinkOnly(backupPathW, nullptr, removeBackupError)) {
            return OperationResult{ false, 4, "rollback-failed", "failed to remove backup link after switch", Win32Details(removeBackupError) };
        }
    }

    std::ostringstream details;
    details << "{";
    details << "\"previousTargetPath\":" << JsonNullableString(before.targetPath) << ",";
    details << "\"finalTargetPath\":" << JsonString(finalInfo.targetPath) << ",";
    details << "\"linkType\":" << JsonString(finalInfo.linkType) << ",";
    details << "\"changed\":[\"currentLink\"],";
    details << "\"rollbackAttempted\":false,";
    details << "\"rollbackSucceeded\":true";
    details << "}";

    return OperationResult{ true, 0, "", "SDK switched", details.str() };
}

OperationResult SwitchSdkOperation(const HelperRequest& request) {
    auto currentPath = GetStringProperty(request.payload, "currentPath");
    auto targetPath = GetStringProperty(request.payload, "targetPath");
    if (!currentPath.has_value() || currentPath->empty()) {
        return OperationResult{ false, 1, "missing-current-path", "missing currentPath", "{\"missing\":[\"currentPath\"]}" };
    }
    if (!targetPath.has_value() || targetPath->empty()) {
        return OperationResult{ false, 1, "missing-target-path", "missing targetPath", "{\"missing\":[\"targetPath\"]}" };
    }

    std::wstring currentPathW = Utf8ToWide(*currentPath);
    std::wstring targetPathW = Utf8ToWide(*targetPath);
    return SwitchSdkCore(currentPathW, targetPathW, *currentPath, request.requestId);
}


// ============================================================================
// 用户环境变量写入（HKCU\Environment）相关辅助与 operation。
// 安全边界：只操作 HKEY_CURRENT_USER，绝不触碰 HKLM，不要求管理员权限。
// 写入统一使用 REG_EXPAND_SZ，保留 %VAR% 占位符不展开。
// ============================================================================

// HKCU\Environment 注册表子键名。当前用户环境变量都存放在该键下。
constexpr wchar_t kUserEnvironmentSubKey[] = L"Environment";
// HKLM 系统环境变量子键名。系统级 PATH 存放在该键下；Windows 新进程会先用系统 PATH，再追加用户 PATH。
// 写入该键需要管理员权限（App 通过 manifest requireAdministrator 提权后再调用 helper）。
constexpr wchar_t kMachineEnvironmentSubKey[] = L"SYSTEM\\CurrentControlSet\\Control\\Session Manager\\Environment";
// 托管 PATH 变量名（大小写不敏感，Windows 注册表惯例为 "Path"）。
constexpr wchar_t kPathValueName[] = L"Path";

// 打开（必要时创建）指定根键下的环境变量子键。
// root 传 HKEY_CURRENT_USER 或 HKEY_LOCAL_MACHINE；access 传所需权限。
// HKLM 写入需要管理员权限，否则 RegCreateKeyExW 返回 ERROR_ACCESS_DENIED。
bool OpenEnvironmentKey(HKEY root, const wchar_t* subKey, REGSAM access, HKEY& key, DWORD& error) {
    // 使用 RegCreateKeyExW：子键不存在时创建，存在则直接打开。
    LONG status = RegCreateKeyExW(
        root,
        subKey,
        0,
        nullptr,
        REG_OPTION_NON_VOLATILE,
        access,
        nullptr,
        &key,
        nullptr);
    if (status != ERROR_SUCCESS) {
        error = static_cast<DWORD>(status);
        return false;
    }

    error = 0;
    return true;
}

// 打开（必要时创建）HKCU\Environment 子键。
// access 传入所需权限（KEY_READ / KEY_READ|KEY_WRITE 等）。
// 普通用户对自身 HKCU 拥有写权限，无需提权。
bool OpenUserEnvironmentKey(REGSAM access, HKEY& key, DWORD& error) {
    return OpenEnvironmentKey(HKEY_CURRENT_USER, kUserEnvironmentSubKey, access, key, error);
}

// 读取注册表字符串值的原始内容（不展开 %VAR%）。
// exists=false 表示该值不存在；返回 true 且 error=0 表示读取流程正常。
bool ReadRegStringRaw(HKEY key, const std::wstring& name, std::wstring& out, bool& exists, DWORD& error) {
    DWORD valueType = 0;
    DWORD byteCount = 0;
    // 第一次查询仅获取字节数与类型。
    LONG status = RegQueryValueExW(key, name.c_str(), nullptr, &valueType, nullptr, &byteCount);
    if (status == ERROR_FILE_NOT_FOUND) {
        exists = false;
        out.clear();
        error = 0;
        return true;
    }
    if (status != ERROR_SUCCESS) {
        error = static_cast<DWORD>(status);
        return false;
    }

    exists = true;
    if (byteCount < sizeof(wchar_t)) {
        // 空字符串值。
        out.clear();
        error = 0;
        return true;
    }

    // 预留一个额外字符确保以 null 结尾，防止注册表中缺少结尾 null 时越界。
    std::vector<wchar_t> buffer(byteCount / sizeof(wchar_t) + 1, L'\0');
    DWORD readBytes = byteCount;
    status = RegQueryValueExW(key, name.c_str(), nullptr, &valueType, reinterpret_cast<LPBYTE>(buffer.data()), &readBytes);
    if (status != ERROR_SUCCESS) {
        error = static_cast<DWORD>(status);
        return false;
    }

    // 以 null 终止符截断，得到稳定字符串。
    out.assign(buffer.data());
    error = 0;
    return true;
}

// 以 REG_EXPAND_SZ 写入字符串值，保留 %VAR% 占位符。
bool WriteRegExpandString(HKEY key, const std::wstring& name, const std::wstring& value, DWORD& error) {
    // 包含结尾 null 一起写入，符合注册表字符串约定。
    const DWORD byteCount = static_cast<DWORD>((value.size() + 1) * sizeof(wchar_t));
    LONG status = RegSetValueExW(
        key,
        name.c_str(),
        0,
        REG_EXPAND_SZ,
        reinterpret_cast<const BYTE*>(value.c_str()),
        byteCount);
    if (status != ERROR_SUCCESS) {
        error = static_cast<DWORD>(status);
        return false;
    }

    error = 0;
    return true;
}

// 规范化单个 PATH 条目用于去重比较：去首尾空白、去尾部分隔符、统一小写。
// NOTE: 仅用于比较，绝不替换写入的原始条目文本，避免改动用户已有书写形式。
std::wstring NormalizePathEntryForCompare(const std::wstring& entry) {
    std::size_t begin = 0;
    std::size_t end = entry.size();
    while (begin < end && (entry[begin] == L' ' || entry[begin] == L'\t')) {
        ++begin;
    }
    while (end > begin && (entry[end - 1] == L' ' || entry[end - 1] == L'\t')) {
        --end;
    }

    std::wstring trimmed = entry.substr(begin, end - begin);
    while (trimmed.size() > 1 && (trimmed.back() == L'\\' || trimmed.back() == L'/')) {
        trimmed.pop_back();
    }

    for (wchar_t& ch : trimmed) {
        ch = static_cast<wchar_t>(towlower(ch));
    }

    return trimmed;
}

// 按 ';' 拆分 PATH 原始值（保留空条目以忠实反映用户原始结构）。
std::vector<std::wstring> SplitPathValue(const std::wstring& value) {
    std::vector<std::wstring> parts;
    std::wstring current;
    for (wchar_t ch : value) {
        if (ch == L';') {
            parts.push_back(current);
            current.clear();
        }
        else {
            current.push_back(ch);
        }
    }
    parts.push_back(current);
    return parts;
}

// 从 payload 中获取数组属性。
const JsonValue* GetArrayProperty(const JsonValue& object, const std::string& name) {
    const JsonValue* value = FindProperty(object, name);
    if (value == nullptr || value->type != JsonType::Array) {
        return nullptr;
    }
    return value;
}

// 把一组变量名拼成 JSON 字符串数组（用于 details，不含敏感值）。
std::string NamesToJsonArray(const std::vector<std::string>& names) {
    std::ostringstream builder;
    builder << "[";
    for (std::size_t index = 0; index < names.size(); ++index) {
        if (index != 0) {
            builder << ",";
        }
        builder << JsonString(names[index]);
    }
    builder << "]";
    return builder.str();
}

// operation: writeUserEnvironment
// payload: {"variables":[{"name":"JAVA_HOME","value":"%DEVSWITCH_HOME%\\current\\java"}, ...]}
// 行为：逐个以 REG_EXPAND_SZ 写入 HKCU\Environment，details 返回已写入变量名（不回显值）。
OperationResult WriteUserEnvironmentOperation(const HelperRequest& request) {
    const JsonValue* variables = GetArrayProperty(request.payload, "variables");
    if (variables == nullptr) {
        return OperationResult{ false, 1, "missing-payload-field", "missing variables", "{\"missing\":[\"variables\"]}" };
    }
    if (variables->arrayValue.empty()) {
        return OperationResult{ false, 1, "missing-payload-field", "variables is empty", "{\"missing\":[\"variables\"]}" };
    }

    // 先校验全部条目结构，避免写入一半才发现非法 payload。
    struct EnvAssignment {
        std::string name;
        std::wstring nameW;
        std::wstring valueW;
    };
    std::vector<EnvAssignment> assignments;
    assignments.reserve(variables->arrayValue.size());

    for (const JsonValue& item : variables->arrayValue) {
        auto name = GetStringProperty(item, "name");
        auto value = GetStringProperty(item, "value");
        if (!name.has_value() || name->empty()) {
            return OperationResult{ false, 1, "invalid-request", "variable name is required", "{\"reason\":\"variable-name-missing\"}" };
        }
        if (!value.has_value()) {
            return OperationResult{ false, 1, "invalid-request", "variable value is required", "{\"reason\":\"variable-value-missing\"}" };
        }
        // 变量名不允许包含 '=' 或 '\0'，避免破坏注册表值语义。
        if (name->find('=') != std::string::npos) {
            return OperationResult{ false, 1, "invalid-request", "variable name contains invalid character", "{\"reason\":\"variable-name-invalid\"}" };
        }
        assignments.push_back(EnvAssignment{ *name, Utf8ToWide(*name), Utf8ToWide(*value) });
    }

    HKEY key = nullptr;
    DWORD openError = 0;
    if (!OpenUserEnvironmentKey(KEY_READ | KEY_WRITE, key, openError)) {
        return OperationResult{ false, 3, "registry-open-failed", "failed to open user environment key", Win32Details(openError) };
    }

    std::vector<std::string> written;
    for (const EnvAssignment& assignment : assignments) {
        DWORD writeError = 0;
        if (!WriteRegExpandString(key, assignment.nameW, assignment.valueW, writeError)) {
            RegCloseKey(key);
            // details 只附带变量名与 win32Error，不含变量值，避免敏感串泄漏到日志。
            std::ostringstream details;
            details << "{\"win32Error\":" << writeError << ",\"failedVariable\":" << JsonString(assignment.name) << "}";
            return OperationResult{ false, 3, "registry-write-failed", "failed to write user environment variable", details.str() };
        }
        written.push_back(assignment.name);
    }

    RegCloseKey(key);

    std::ostringstream details;
    details << "{\"written\":" << NamesToJsonArray(written) << ",\"count\":" << written.size() << "}";
    return OperationResult{ true, 0, "", "user environment variables written", details.str() };
}

// operation: appendManagedPathEntries
// payload: {"entries":["%DEVSWITCH_HOME%\\current\\java\\bin", ...]}
// 行为：把托管片段追加到 HKCU\Environment\Path，去重、只加不存在的、保持已有顺序，
//       绝不删除或重排用户已有条目。Path 以 REG_EXPAND_SZ 写入。
OperationResult AppendManagedPathEntriesOperation(const HelperRequest& request) {
    const JsonValue* entries = GetArrayProperty(request.payload, "entries");
    if (entries == nullptr) {
        return OperationResult{ false, 1, "missing-payload-field", "missing entries", "{\"missing\":[\"entries\"]}" };
    }
    if (entries->arrayValue.empty()) {
        return OperationResult{ false, 1, "missing-payload-field", "entries is empty", "{\"missing\":[\"entries\"]}" };
    }

    std::vector<std::wstring> requested;
    requested.reserve(entries->arrayValue.size());
    for (const JsonValue& item : entries->arrayValue) {
        if (item.type != JsonType::String || item.stringValue.empty()) {
            return OperationResult{ false, 1, "invalid-request", "path entry must be a non-empty string", "{\"reason\":\"path-entry-invalid\"}" };
        }
        requested.push_back(Utf8ToWide(item.stringValue));
    }

    HKEY key = nullptr;
    DWORD openError = 0;
    if (!OpenUserEnvironmentKey(KEY_READ | KEY_WRITE, key, openError)) {
        return OperationResult{ false, 3, "registry-open-failed", "failed to open user environment key", Win32Details(openError) };
    }

    bool pathExists = false;
    std::wstring rawPath;
    DWORD readError = 0;
    if (!ReadRegStringRaw(key, kPathValueName, rawPath, pathExists, readError)) {
        RegCloseKey(key);
        return OperationResult{ false, 3, "registry-read-failed", "failed to read user Path", Win32Details(readError) };
    }

    // 收集现有条目的规范化形式用于去重（绝不修改原始 rawPath）。
    std::vector<std::wstring> existingNormalized;
    for (const std::wstring& part : SplitPathValue(rawPath)) {
        std::wstring normalized = NormalizePathEntryForCompare(part);
        if (!normalized.empty()) {
            existingNormalized.push_back(normalized);
        }
    }

    auto alreadyPresent = [&existingNormalized](const std::wstring& normalized) {
        for (const std::wstring& existing : existingNormalized) {
            if (existing == normalized) {
                return true;
            }
        }
        return false;
    };

    std::vector<std::wstring> added;
    std::vector<std::string> addedUtf8;
    std::wstring merged = rawPath;
    for (const std::wstring& entry : requested) {
        std::wstring normalized = NormalizePathEntryForCompare(entry);
        if (normalized.empty() || alreadyPresent(normalized)) {
            continue;
        }

        // 只在末尾追加，保留原始值结构，永不重排用户原有条目。
        if (merged.empty()) {
            merged = entry;
        }
        else {
            if (merged.back() != L';') {
                merged += L';';
            }
            merged += entry;
        }

        // 把新增项纳入去重集合，避免同一请求内重复 entry 被加两次。
        existingNormalized.push_back(normalized);
        added.push_back(entry);
        addedUtf8.push_back(WideToUtf8(entry));
    }

    bool changed = !added.empty();
    if (changed) {
        DWORD writeError = 0;
        if (!WriteRegExpandString(key, kPathValueName, merged, writeError)) {
            RegCloseKey(key);
            return OperationResult{ false, 3, "registry-write-failed", "failed to write user Path", Win32Details(writeError) };
        }
    }

    RegCloseKey(key);

    // details 只列出新增片段与计数，不回显完整 Path（脱敏意识）。
    std::ostringstream details;
    details << "{\"added\":" << NamesToJsonArray(addedUtf8) << ",\"count\":" << added.size() << ",\"changed\":" << BoolJson(changed) << "}";
    return OperationResult{ true, 0, "", "managed path entries appended", details.str() };
}

// operation 核心：从指定环境键的 Path 移除与托管片段完全匹配（规范化相等）的条目。
// scopeLabel 仅用于错误信息（"user" / "machine"）。
OperationResult RemoveManagedPathEntriesCore(const HelperRequest& request, HKEY root, const wchar_t* subKey, const char* scopeLabel) {
    const JsonValue* entries = GetArrayProperty(request.payload, "entries");
    if (entries == nullptr) {
        return OperationResult{ false, 1, "missing-payload-field", "missing entries", "{\"missing\":[\"entries\"]}" };
    }
    if (entries->arrayValue.empty()) {
        return OperationResult{ false, 1, "missing-payload-field", "entries is empty", "{\"missing\":[\"entries\"]}" };
    }

    // 待移除托管片段的规范化集合。
    std::vector<std::wstring> targets;
    for (const JsonValue& item : entries->arrayValue) {
        if (item.type != JsonType::String || item.stringValue.empty()) {
            return OperationResult{ false, 1, "invalid-request", "path entry must be a non-empty string", "{\"reason\":\"path-entry-invalid\"}" };
        }
        std::wstring normalized = NormalizePathEntryForCompare(Utf8ToWide(item.stringValue));
        if (!normalized.empty()) {
            targets.push_back(normalized);
        }
    }

    HKEY key = nullptr;
    DWORD openError = 0;
    if (!OpenEnvironmentKey(root, subKey, KEY_READ | KEY_WRITE, key, openError)) {
        // HKLM 写入被拒说明 helper 未提权；用 access-denied 错误码让上层提示需要管理员。
        const char* code = (openError == ERROR_ACCESS_DENIED) ? "registry-access-denied" : "registry-open-failed";
        std::string message = std::string("failed to open ") + scopeLabel + " environment key";
        return OperationResult{ false, 3, code, message, Win32Details(openError) };
    }

    bool pathExists = false;
    std::wstring rawPath;
    DWORD readError = 0;
    if (!ReadRegStringRaw(key, kPathValueName, rawPath, pathExists, readError)) {
        RegCloseKey(key);
        return OperationResult{ false, 3, "registry-read-failed", "failed to read Path", Win32Details(readError) };
    }

    auto isTarget = [&targets](const std::wstring& normalized) {
        for (const std::wstring& target : targets) {
            if (target == normalized) {
                return true;
            }
        }
        return false;
    };

    std::vector<std::wstring> kept;
    std::vector<std::string> removed;
    for (const std::wstring& part : SplitPathValue(rawPath)) {
        std::wstring normalized = NormalizePathEntryForCompare(part);
        // 只移除完全匹配的托管条目；空条目与非托管条目原样保留。
        if (!normalized.empty() && isTarget(normalized)) {
            removed.push_back(WideToUtf8(part));
            continue;
        }
        kept.push_back(part);
    }

    bool changed = !removed.empty();
    if (changed) {
        // 用 ';' 重新拼接保留下来的条目。
        std::wstring rebuilt;
        for (std::size_t index = 0; index < kept.size(); ++index) {
            if (index != 0) {
                rebuilt += L';';
            }
            rebuilt += kept[index];
        }

        DWORD writeError = 0;
        if (!WriteRegExpandString(key, kPathValueName, rebuilt, writeError)) {
            RegCloseKey(key);
            const char* code = (writeError == ERROR_ACCESS_DENIED) ? "registry-access-denied" : "registry-write-failed";
            return OperationResult{ false, 3, code, "failed to write Path", Win32Details(writeError) };
        }
    }

    RegCloseKey(key);

    std::ostringstream details;
    details << "{\"removed\":" << NamesToJsonArray(removed) << ",\"count\":" << removed.size() << ",\"changed\":" << BoolJson(changed) << "}";
    return OperationResult{ true, 0, "", "managed path entries removed", details.str() };
}

// operation: removeManagedPathEntries（用于 reset）
// payload: {"entries":["%DEVSWITCH_HOME%\\current\\java\\bin", ...]}
// 行为：仅从 HKCU Path 中移除与托管片段完全匹配（规范化后相等）的条目，保留其它用户条目与顺序。
OperationResult RemoveManagedPathEntriesOperation(const HelperRequest& request) {
    return RemoveManagedPathEntriesCore(request, HKEY_CURRENT_USER, kUserEnvironmentSubKey, "user");
}

// operation: removeMachinePathEntries（用于 reset 系统级托管片段）
// payload: {"entries":["...\\current\\java\\bin", ...]}
// 行为：从 HKLM 系统 Path 移除托管片段。需要管理员权限；无权限时返回 registry-access-denied。
OperationResult RemoveMachinePathEntriesOperation(const HelperRequest& request) {
    return RemoveManagedPathEntriesCore(request, HKEY_LOCAL_MACHINE, kMachineEnvironmentSubKey, "machine");
}

// operation 核心：把托管片段“置顶”到指定环境键的 Path 最前面。
// scopeLabel 仅用于错误信息（"user" / "machine"）。
OperationResult PrependManagedPathEntriesCore(const HelperRequest& request, HKEY root, const wchar_t* subKey, const char* scopeLabel) {
    const JsonValue* entries = GetArrayProperty(request.payload, "entries");
    if (entries == nullptr) {
        return OperationResult{ false, 1, "missing-payload-field", "missing entries", "{\"missing\":[\"entries\"]}" };
    }
    if (entries->arrayValue.empty()) {
        return OperationResult{ false, 1, "missing-payload-field", "entries is empty", "{\"missing\":[\"entries\"]}" };
    }

    // 收集请求片段并按规范化形式去重，保留“首次出现”的原始字符串与顺序，
    // 用于：(a) 构造最前面的置顶序列；(b) 作为从原 Path 中剔除旧位置同名项的匹配集合。
    std::vector<std::wstring> dedupedRequested;   // 去重后的原始片段（写入用，保序）
    std::vector<std::wstring> requestedNormalized; // 与 dedupedRequested 一一对应的规范化形式（匹配用）
    for (const JsonValue& item : entries->arrayValue) {
        if (item.type != JsonType::String || item.stringValue.empty()) {
            return OperationResult{ false, 1, "invalid-request", "path entry must be a non-empty string", "{\"reason\":\"path-entry-invalid\"}" };
        }
        std::wstring entry = Utf8ToWide(item.stringValue);
        std::wstring normalized = NormalizePathEntryForCompare(entry);
        // 规范化后为空（例如纯分隔符/空白）无意义，直接跳过。
        if (normalized.empty()) {
            continue;
        }
        // 同一请求内出现重复片段时，只保留首个，避免置顶序列里出现重复项。
        bool seen = false;
        for (const std::wstring& existing : requestedNormalized) {
            if (existing == normalized) {
                seen = true;
                break;
            }
        }
        if (seen) {
            continue;
        }
        dedupedRequested.push_back(entry);
        requestedNormalized.push_back(normalized);
    }

    if (dedupedRequested.empty()) {
        return OperationResult{ false, 1, "invalid-request", "no valid path entry provided", "{\"reason\":\"path-entry-invalid\"}" };
    }

    HKEY key = nullptr;
    DWORD openError = 0;
    if (!OpenEnvironmentKey(root, subKey, KEY_READ | KEY_WRITE, key, openError)) {
        // HKLM 写入被拒说明 helper 未提权；用 access-denied 错误码让上层提示需要管理员。
        const char* code = (openError == ERROR_ACCESS_DENIED) ? "registry-access-denied" : "registry-open-failed";
        std::string message = std::string("failed to open ") + scopeLabel + " environment key";
        return OperationResult{ false, 3, code, message, Win32Details(openError) };
    }

    bool pathExists = false;
    std::wstring rawPath;
    DWORD readError = 0;
    if (!ReadRegStringRaw(key, kPathValueName, rawPath, pathExists, readError)) {
        RegCloseKey(key);
        return OperationResult{ false, 3, "registry-read-failed", "failed to read Path", Win32Details(readError) };
    }

    // 判断某条目规范化形式是否属于请求集合（即托管片段，需从原位置剔除）。
    auto isRequested = [&requestedNormalized](const std::wstring& normalized) {
        for (const std::wstring& target : requestedNormalized) {
            if (target == normalized) {
                return true;
            }
        }
        return false;
    };

    // 保留原 Path 中“非请求托管片段”的条目，维持其相对顺序与原始字符串（含空条目、非托管项）。
    std::vector<std::wstring> kept;
    for (const std::wstring& part : SplitPathValue(rawPath)) {
        std::wstring normalized = NormalizePathEntryForCompare(part);
        // 规范化后等于某请求片段的条目一律移除（去掉旧位置/重复的托管片段）；
        // 空条目与非托管用户条目原样保留。
        if (!normalized.empty() && isRequested(normalized)) {
            continue;
        }
        kept.push_back(part);
    }

    // 新 Path = [按请求顺序去重的托管片段] + [保留的原条目]。用 ';' 拼接。
    std::vector<std::wstring> finalEntries;
    finalEntries.reserve(dedupedRequested.size() + kept.size());
    for (const std::wstring& entry : dedupedRequested) {
        finalEntries.push_back(entry);
    }
    for (const std::wstring& entry : kept) {
        finalEntries.push_back(entry);
    }

    std::wstring rebuilt;
    for (std::size_t index = 0; index < finalEntries.size(); ++index) {
        if (index != 0) {
            rebuilt += L';';
        }
        rebuilt += finalEntries[index];
    }

    // 仅当重建结果与原值不同才写回，避免无意义的注册表写入（例如片段已在最前且无残留）。
    bool changed = (rebuilt != rawPath);
    if (changed) {
        DWORD writeError = 0;
        if (!WriteRegExpandString(key, kPathValueName, rebuilt, writeError)) {
            RegCloseKey(key);
            const char* code = (writeError == ERROR_ACCESS_DENIED) ? "registry-access-denied" : "registry-write-failed";
            return OperationResult{ false, 3, code, "failed to write Path", Win32Details(writeError) };
        }
    }

    RegCloseKey(key);

    // details 只列出被置顶的托管片段与计数，不回显完整 Path（脱敏意识）。
    std::vector<std::string> prependedUtf8;
    prependedUtf8.reserve(dedupedRequested.size());
    for (const std::wstring& entry : dedupedRequested) {
        prependedUtf8.push_back(WideToUtf8(entry));
    }

    std::ostringstream details;
    details << "{\"prepended\":" << NamesToJsonArray(prependedUtf8) << ",\"count\":" << prependedUtf8.size() << ",\"changed\":" << BoolJson(changed) << "}";
    return OperationResult{ true, 0, "", "managed path entries prepended", details.str() };
}

// operation: prependManagedPathEntries
// payload: {"entries":["%DEVSWITCH_HOME%\\current\\java\\bin", ...]}
// 行为：把托管片段“置顶”到 HKCU\Environment\Path 最前面，确保不被同账户用户 Path 里的同名 SDK 残留遮蔽。
OperationResult PrependManagedPathEntriesOperation(const HelperRequest& request) {
    return PrependManagedPathEntriesCore(request, HKEY_CURRENT_USER, kUserEnvironmentSubKey, "user");
}

// operation: prependMachinePathEntries
// payload: {"entries":["...\\current\\java\\bin", ...]}
// 行为：把托管片段“置顶”到 HKLM 系统 Path 最前面。Windows 新进程的有效 PATH 是 系统 Path 在前、用户 Path 在后，
//       因此只有写到系统 Path 最前，才能压过系统级旧 JDK/Node/Go 条目（如 D:\Programs\java\jdk\...\bin）。
//       需要管理员权限；无权限时返回 registry-access-denied，由上层提示用户以管理员身份运行。
OperationResult PrependMachinePathEntriesOperation(const HelperRequest& request) {
    return PrependManagedPathEntriesCore(request, HKEY_LOCAL_MACHINE, kMachineEnvironmentSubKey, "machine");
}

// operation: readUserEnvironment（辅助：doctor / 测试读回）
// payload: {"names":["JAVA_HOME","Path"]}
// 行为：读取指定变量的 REG_EXPAND_SZ 原始未展开值，details 返回 values 映射。
OperationResult ReadUserEnvironmentOperation(const HelperRequest& request) {
    const JsonValue* names = GetArrayProperty(request.payload, "names");
    if (names == nullptr) {
        return OperationResult{ false, 1, "missing-payload-field", "missing names", "{\"missing\":[\"names\"]}" };
    }
    if (names->arrayValue.empty()) {
        return OperationResult{ false, 1, "missing-payload-field", "names is empty", "{\"missing\":[\"names\"]}" };
    }

    std::vector<std::string> requestedNames;
    for (const JsonValue& item : names->arrayValue) {
        if (item.type != JsonType::String || item.stringValue.empty()) {
            return OperationResult{ false, 1, "invalid-request", "name must be a non-empty string", "{\"reason\":\"name-invalid\"}" };
        }
        requestedNames.push_back(item.stringValue);
    }

    HKEY key = nullptr;
    DWORD openError = 0;
    if (!OpenUserEnvironmentKey(KEY_READ, key, openError)) {
        return OperationResult{ false, 3, "registry-open-failed", "failed to open user environment key", Win32Details(openError) };
    }

    std::ostringstream values;
    values << "{";
    bool first = true;
    for (const std::string& name : requestedNames) {
        bool exists = false;
        std::wstring raw;
        DWORD readError = 0;
        if (!ReadRegStringRaw(key, Utf8ToWide(name), raw, exists, readError)) {
            RegCloseKey(key);
            std::ostringstream details;
            details << "{\"win32Error\":" << readError << ",\"failedVariable\":" << JsonString(name) << "}";
            return OperationResult{ false, 3, "registry-read-failed", "failed to read user environment variable", details.str() };
        }

        if (!first) {
            values << ",";
        }
        first = false;
        // 读操作明确需要返回原始值（doctor 与测试都依赖），故 details 中包含未展开值。
        values << JsonString(name) << ":{\"exists\":" << BoolJson(exists) << ",\"value\":" << (exists ? JsonString(WideToUtf8(raw)) : std::string("null")) << "}";
    }
    values << "}";

    RegCloseKey(key);

    std::ostringstream details;
    details << "{\"values\":" << values.str() << "}";
    return OperationResult{ true, 0, "", "user environment variables read", details.str() };
}

// operation: broadcastEnvironmentChanged
// 行为：向所有顶层窗口广播 WM_SETTINGCHANGE，lParam="Environment"，让新进程感知用户环境变化。
// 使用 SMTO_ABORTIFHUNG + 5000ms 超时，避免被无响应窗口挂死 helper 进程。
// operation: broadcastEnvironmentChanged
// 行为：向所有顶层窗口广播 WM_SETTINGCHANGE，lParam="Environment"，让新进程感知用户环境变化。
// 使用 SMTO_ABORTIFHUNG + 1000ms 超时：环境广播本质是“尽力而为”的通知，1 秒足够正常窗口响应，
// 超时上限从 5000 降到 1000 可显著减少切换时被无响应窗口拖慢的卡顿（最坏 5s→1s）。
bool DoBroadcastEnvironmentChange(DWORD& win32Error) {
    DWORD_PTR result = 0;
    LRESULT sent = SendMessageTimeoutW(
        HWND_BROADCAST,
        WM_SETTINGCHANGE,
        0,
        reinterpret_cast<LPARAM>(L"Environment"),
        SMTO_ABORTIFHUNG,
        1000,
        &result);
    if (sent == 0) {
        win32Error = GetLastError();
        return false;
    }
    win32Error = 0;
    return true;
}

OperationResult BroadcastEnvironmentChangedOperation(const HelperRequest& /*request*/) {
    DWORD error = 0;
    if (!DoBroadcastEnvironmentChange(error)) {
        // 返回 0 表示失败或超时；附带 win32Error 供诊断。
        return OperationResult{ false, 3, "broadcast-failed", "failed to broadcast environment change", Win32Details(error) };
    }

    return OperationResult{ true, 0, "", "environment change broadcasted", "{\"broadcast\":true}" };
}

// ============================================================================
// shim 单目录方案：rebuildShims / switchSdkBatch。
// shim 是一个通用转发器（DevSwitch.Shim.exe），被复制成 shims\<cmd>.exe 后由 PATH 命中，
// 运行时自定位 dataRoot 并转发到 current\<type>\bin 下的真实可执行。
// 好处：系统 PATH 只需 1 个 shims 目录即可覆盖所有命令，根治 2047 上限；切换只换 junction，无需改 PATH。
// ============================================================================

// 枚举目录下的可执行文件名（.exe/.cmd/.bat/.com），返回去扩展名后的 stem 集合（小写去重）。
std::vector<std::wstring> EnumerateExecutableStems(const std::wstring& dir) {
    std::vector<std::wstring> stems;
    std::wstring pattern = CombinePath(dir, L"*");
    WIN32_FIND_DATAW fd{};
    HANDLE h = FindFirstFileW(pattern.c_str(), &fd);
    if (h == INVALID_HANDLE_VALUE) {
        return stems;
    }
    do {
        if (fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) {
            continue;
        }
        std::wstring name = fd.cFileName;
        std::size_t dot = name.find_last_of(L'.');
        if (dot == std::wstring::npos || dot == 0) {
            continue;
        }
        std::wstring ext = name.substr(dot);
        for (wchar_t& ch : ext) {
            ch = static_cast<wchar_t>(towlower(ch));
        }
        if (ext != L".exe" && ext != L".cmd" && ext != L".bat" && ext != L".com") {
            continue;
        }
        std::wstring stem = name.substr(0, dot);
        std::wstring stemLower = stem;
        for (wchar_t& ch : stemLower) {
            ch = static_cast<wchar_t>(towlower(ch));
        }
        bool seen = false;
        for (const std::wstring& existing : stems) {
            std::wstring el = existing;
            for (wchar_t& ch : el) {
                ch = static_cast<wchar_t>(towlower(ch));
            }
            if (el == stemLower) {
                seen = true;
                break;
            }
        }
        if (!seen) {
            stems.push_back(stem);
        }
    } while (FindNextFileW(h, &fd));
    FindClose(h);
    return stems;
}

// 收集某 dataRoot 下应该存在的全部 shim stem：按 java/maven/node/go 的 current\<type>\bin 顺序枚举，整体去重。
std::vector<std::wstring> CollectDesiredShimStems(const std::wstring& dataRoot) {
    std::wstring current = CombinePath(dataRoot, L"current");
    std::vector<std::wstring> dirs = {
        CombinePath(CombinePath(current, L"java"), L"bin"),
        CombinePath(CombinePath(current, L"maven"), L"bin"),
        CombinePath(current, L"node"),
        CombinePath(CombinePath(current, L"go"), L"bin"),
    };

    std::vector<std::wstring> all;
    for (const std::wstring& dir : dirs) {
        for (const std::wstring& stem : EnumerateExecutableStems(dir)) {
            std::wstring stemLower = stem;
            for (wchar_t& ch : stemLower) {
                ch = static_cast<wchar_t>(towlower(ch));
            }
            bool seen = false;
            for (const std::wstring& existing : all) {
                std::wstring el = existing;
                for (wchar_t& ch : el) {
                    ch = static_cast<wchar_t>(towlower(ch));
                }
                if (el == stemLower) {
                    seen = true;
                    break;
                }
            }
            if (!seen) {
                all.push_back(stem);
            }
        }
    }
    return all;
}

// operation: rebuildShims
// payload: {"dataRoot":"...","shimSourcePath":"...\\DevSwitch.Shim.exe"}
// 行为：根据 current\<type>\bin 下的真实可执行，在 dataRoot\shims 下生成/更新 <stem>.exe（复制 shim 转发器），
//       并删除目标已不存在的陈旧 shim。仅操作 shims 目录，绝不碰真实 SDK 与其它文件。
OperationResult RebuildShimsOperation(const HelperRequest& request) {
    auto dataRoot = GetStringProperty(request.payload, "dataRoot");
    auto shimSource = GetStringProperty(request.payload, "shimSourcePath");
    if (!dataRoot.has_value() || dataRoot->empty()) {
        return OperationResult{ false, 1, "missing-payload-field", "missing dataRoot", "{\"missing\":[\"dataRoot\"]}" };
    }
    if (!shimSource.has_value() || shimSource->empty()) {
        return OperationResult{ false, 1, "missing-payload-field", "missing shimSourcePath", "{\"missing\":[\"shimSourcePath\"]}" };
    }

    std::wstring dataRootW = Utf8ToWide(*dataRoot);
    std::wstring shimSourceW = Utf8ToWide(*shimSource);
    if (GetFileAttributesW(shimSourceW.c_str()) == INVALID_FILE_ATTRIBUTES) {
        return OperationResult{ false, 3, "shim-source-missing", "shim source executable not found", JsonString(*shimSource) };
    }

    std::wstring shimsDir = CombinePath(dataRootW, L"shims");
    DWORD dirError = 0;
    if (!EnsureDirectoryRecursive(shimsDir, dirError)) {
        return OperationResult{ false, 3, "create-shims-dir-failed", "failed to create shims directory", Win32Details(dirError) };
    }

    std::vector<std::wstring> desired = CollectDesiredShimStems(dataRootW);

    // 1) 生成/更新 shim：把转发器复制成 <stem>.exe（始终覆盖，确保转发器版本一致）。
    std::vector<std::string> created;
    for (const std::wstring& stem : desired) {
        std::wstring dest = CombinePath(shimsDir, stem + L".exe");
        if (!CopyFileW(shimSourceW.c_str(), dest.c_str(), FALSE)) {
            DWORD copyError = GetLastError();
            return OperationResult{ false, 3, "shim-copy-failed", "failed to copy shim", Win32Details(copyError) };
        }
        created.push_back(WideToUtf8(stem));
    }

    // 2) 清理陈旧 shim：shims\*.exe 中 stem 不在 desired 集合的删除（仅删 shims 目录内的 .exe）。
    std::vector<std::string> removed;
    {
        std::wstring pattern = CombinePath(shimsDir, L"*.exe");
        WIN32_FIND_DATAW fd{};
        HANDLE h = FindFirstFileW(pattern.c_str(), &fd);
        if (h != INVALID_HANDLE_VALUE) {
            do {
                if (fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) {
                    continue;
                }
                std::wstring name = fd.cFileName;
                std::size_t dot = name.find_last_of(L'.');
                std::wstring stem = (dot == std::wstring::npos) ? name : name.substr(0, dot);
                std::wstring stemLower = stem;
                for (wchar_t& ch : stemLower) {
                    ch = static_cast<wchar_t>(towlower(ch));
                }
                bool wanted = false;
                for (const std::wstring& d : desired) {
                    std::wstring dl = d;
                    for (wchar_t& ch : dl) {
                        ch = static_cast<wchar_t>(towlower(ch));
                    }
                    if (dl == stemLower) {
                        wanted = true;
                        break;
                    }
                }
                if (!wanted) {
                    std::wstring stale = CombinePath(shimsDir, name);
                    if (DeleteFileW(stale.c_str())) {
                        removed.push_back(WideToUtf8(name));
                    }
                }
            } while (FindNextFileW(h, &fd));
            FindClose(h);
        }
    }

    std::ostringstream details;
    details << "{\"shimsDir\":" << JsonString(WideToUtf8(shimsDir)) << ",";
    details << "\"created\":" << NamesToJsonArray(created) << ",";
    details << "\"createdCount\":" << created.size() << ",";
    details << "\"removed\":" << NamesToJsonArray(removed) << ",";
    details << "\"removedCount\":" << removed.size() << "}";
    return OperationResult{ true, 0, "", "shims rebuilt", details.str() };
}

// operation: switchSdkBatch
// payload: {"currentPath":"...","targetPath":"...","broadcast":true}
// 行为：在单个 helper 进程内完成 junction 切换 + （可选）一次广播。
//       把原本 6~8 个 helper 进程合并为 1 个，显著降低切换耗时与卡顿。
OperationResult SwitchSdkBatchOperation(const HelperRequest& request) {
    auto currentPath = GetStringProperty(request.payload, "currentPath");
    auto targetPath = GetStringProperty(request.payload, "targetPath");
    if (!currentPath.has_value() || currentPath->empty()) {
        return OperationResult{ false, 1, "missing-current-path", "missing currentPath", "{\"missing\":[\"currentPath\"]}" };
    }
    if (!targetPath.has_value() || targetPath->empty()) {
        return OperationResult{ false, 1, "missing-target-path", "missing targetPath", "{\"missing\":[\"targetPath\"]}" };
    }

    std::wstring currentPathW = Utf8ToWide(*currentPath);
    std::wstring targetPathW = Utf8ToWide(*targetPath);

    OperationResult switchResult = SwitchSdkCore(currentPathW, targetPathW, *currentPath, request.requestId);
    if (!switchResult.success) {
        return switchResult;
    }

    // broadcast 默认 true。
    bool broadcast = true;
    const JsonValue* broadcastValue = FindProperty(request.payload, "broadcast");
    if (broadcastValue != nullptr && broadcastValue->type == JsonType::Bool) {
        broadcast = broadcastValue->boolValue;
    }

    bool broadcasted = false;
    if (broadcast) {
        DWORD broadcastError = 0;
        broadcasted = DoBroadcastEnvironmentChange(broadcastError);
        // 广播失败不影响切换成功结论（环境通知是尽力而为），仅在 details 标记。
    }

    std::ostringstream details;
    details << "{\"switched\":true,\"finalTargetPath\":" << JsonString(*targetPath)
            << ",\"broadcasted\":" << BoolJson(broadcasted) << "}";
    return OperationResult{ true, 0, "", "SDK switched (batch)", details.str() };
}

OperationResult DispatchOperation(const HelperRequest& request) {
    if (request.operation == "ping") {
        return OperationResult{ true, 0, "", "pong", "{\"protocolVersion\":1}" };
    }

    if (request.operation == "inspectLink") {
        return InspectLinkOperation(request);
    }

    if (request.operation == "createCurrentLink") {
        return CreateCurrentLinkOperation(request);
    }

    if (request.operation == "removeCurrentLink") {
        return RemoveCurrentLinkOperation(request);
    }

    if (request.operation == "switchSdk") {
        return SwitchSdkOperation(request);
    }

    if (request.operation == "switchSdkBatch") {
        return SwitchSdkBatchOperation(request);
    }

    if (request.operation == "rebuildShims") {
        return RebuildShimsOperation(request);
    }

    if (request.operation == "writeUserEnvironment") {
        return WriteUserEnvironmentOperation(request);
    }

    if (request.operation == "appendManagedPathEntries") {
        return AppendManagedPathEntriesOperation(request);
    }

    if (request.operation == "removeManagedPathEntries") {
        return RemoveManagedPathEntriesOperation(request);
    }

    if (request.operation == "prependManagedPathEntries") {
        return PrependManagedPathEntriesOperation(request);
    }

    if (request.operation == "prependMachinePathEntries") {
        return PrependMachinePathEntriesOperation(request);
    }

    if (request.operation == "removeMachinePathEntries") {
        return RemoveMachinePathEntriesOperation(request);
    }

    if (request.operation == "readUserEnvironment") {
        return ReadUserEnvironmentOperation(request);
    }

    if (request.operation == "broadcastEnvironmentChanged") {
        return BroadcastEnvironmentChangedOperation(request);
    }

    return OperationResult{ false, 2, "unknown-operation", "unknown operation", "{\"operation\":" + JsonString(request.operation) + "}" };
}

} // namespace

// CLI 提权模式：当 App 以 asInvoker 运行、需要一次性写 HKLM 系统 PATH 时，
// 通过 ShellExecute "runas" 提权启动 helper 并用命令行参数传递（提权进程不便从非提权父进程读 stdin 管道）。
//   DevSwitch.Helper.exe --install-machine-path "<shimsDir>"  → 把 shimsDir 置顶到系统 PATH 并广播
//   DevSwitch.Helper.exe --remove-machine-path  "<shimsDir>"  → 从系统 PATH 移除 shimsDir 并广播
// 退出码：0 成功；非 0 失败（5=权限不足，与 access-denied 对应）。
namespace {

OperationResult MachinePathCliCore(const std::wstring& shimsDir, bool prepend) {
    // 构造一个等价的 HelperRequest，复用既有 prepend/remove machine 核心逻辑。
    HelperRequest req;
    req.requestId = "cli";
    JsonValue entry;
    entry.type = JsonType::String;
    entry.stringValue = WideToUtf8(shimsDir);
    JsonValue arr;
    arr.type = JsonType::Array;
    arr.arrayValue.push_back(entry);
    req.payload.type = JsonType::Object;
    req.payload.objectValue["entries"] = arr;

    OperationResult pathResult = prepend
        ? PrependManagedPathEntriesCore(req, HKEY_LOCAL_MACHINE, kMachineEnvironmentSubKey, "machine")
        : RemoveManagedPathEntriesCore(req, HKEY_LOCAL_MACHINE, kMachineEnvironmentSubKey, "machine");

    if (pathResult.success) {
        DWORD broadcastError = 0;
        DoBroadcastEnvironmentChange(broadcastError);
    }
    return pathResult;
}

int RunMachinePathCli(const std::wstring& mode, const std::wstring& shimsDir) {
    if (shimsDir.empty()) {
        std::cout << "{\"success\":false,\"errorCode\":\"invalid-request\",\"message\":\"missing shims dir\"}" << std::endl;
        return 1;
    }

    bool prepend = (mode == L"--install-machine-path");
    OperationResult result = MachinePathCliCore(shimsDir, prepend);
    std::cout << BuildResponseJson("cli", result.success, result.errorCode, result.message, result.detailsJson) << std::endl;
    if (result.success) {
        return 0;
    }
    // 权限不足映射为退出码 5（ERROR_ACCESS_DENIED），便于上层区分。
    return (result.errorCode == "registry-access-denied") ? 5 : 1;
}

} // namespace

int main() {
    // CLI 提权模式：识别 --install-machine-path / --remove-machine-path。
    // 用 GetCommandLineW 取宽字符参数，避免依赖 wmain（保持现有 g++ 构建参数不变，无需 -municode）。
    {
        int wargc = 0;
        LPWSTR* wargv = CommandLineToArgvW(GetCommandLineW(), &wargc);
        if (wargv != nullptr) {
            if (wargc >= 3) {
                std::wstring mode = wargv[1];
                if (mode == L"--install-machine-path" || mode == L"--remove-machine-path") {
                    std::wstring shimsDir = wargv[2];
                    LocalFree(wargv);
                    return RunMachinePathCli(mode, shimsDir);
                }
            }
            LocalFree(wargv);
        }
    }

    // 默认 stdin JSON 协议模式（一次请求一进程）。
    const std::string input = ReadAllStdin();
    const ParseRequestResult parseResult = ParseRequest(input);

    if (parseResult.invalidJson) {
        std::cout << BuildResponseJson("", false, "invalid-json", parseResult.message.empty() ? "invalid JSON" : parseResult.message, parseResult.detailsJson) << std::endl;
        return 1;
    }

    if (!parseResult.ok) {
        std::cout << BuildResponseJson(parseResult.requestId, false, "invalid-request", parseResult.message.empty() ? "invalid request" : parseResult.message, parseResult.detailsJson.empty() ? "{}" : parseResult.detailsJson) << std::endl;
        return 1;
    }

    OperationResult operationResult = DispatchOperation(parseResult.request);
    std::cout << BuildResponseJson(parseResult.request.requestId, operationResult.success, operationResult.errorCode, operationResult.message, operationResult.detailsJson) << std::endl;
    return operationResult.exitCode;
}


