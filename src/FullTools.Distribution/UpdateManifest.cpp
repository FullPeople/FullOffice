#include "UpdateManifest.h"
#include "UpdatePublicKey.h"
#include <windows.h>
#include <bcrypt.h>
#include <wincrypt.h>
#include <array>
#include <algorithm>
#include <charconv>
#include <map>
#include <vector>

namespace fulltools {
namespace {
struct Algorithm { BCRYPT_ALG_HANDLE value = nullptr; ~Algorithm() { if (value) BCryptCloseAlgorithmProvider(value, 0); } };
struct Key { BCRYPT_KEY_HANDLE value = nullptr; ~Key() { if (value) BCryptDestroyKey(value); } };
struct Hash { BCRYPT_HASH_HANDLE value = nullptr; ~Hash() { if (value) BCryptDestroyHash(value); } };
bool decode(std::string_view text, std::vector<std::uint8_t>& bytes) {
    DWORD length = 0;
    if (text.size() > 65536 || !CryptStringToBinaryA(text.data(), static_cast<DWORD>(text.size()), CRYPT_STRING_BASE64 | CRYPT_STRING_STRICT,
            nullptr, &length, nullptr, nullptr)) return false;
    bytes.resize(length);
    return CryptStringToBinaryA(text.data(), static_cast<DWORD>(text.size()), CRYPT_STRING_BASE64 | CRYPT_STRING_STRICT,
        bytes.data(), &length, nullptr, nullptr) != FALSE;
}
bool digest(const std::vector<std::uint8_t>& body, std::array<std::uint8_t, 32>& result) {
    Algorithm algorithm;
    if (BCryptOpenAlgorithmProvider(&algorithm.value, BCRYPT_SHA256_ALGORITHM, nullptr, 0) != 0) return false;
    Hash hash;
    if (BCryptCreateHash(algorithm.value, &hash.value, nullptr, 0, nullptr, 0, 0) != 0) return false;
    return BCryptHashData(hash.value, const_cast<PUCHAR>(body.data()), static_cast<ULONG>(body.size()), 0) == 0 &&
        BCryptFinishHash(hash.value, result.data(), static_cast<ULONG>(result.size()), 0) == 0;
}
struct Version { std::array<unsigned, 3> numeric{}; std::vector<std::string_view> prerelease; };
bool identifiers(std::string_view text, bool prerelease, std::vector<std::string_view>& output) {
    if (text.empty()) return false;
    size_t begin = 0;
    for (;;) {
        const auto end = text.find('.', begin);
        const auto part = text.substr(begin, end == std::string_view::npos ? end : end - begin);
        if (part.empty() || part.find_first_not_of("0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz-") != std::string_view::npos) return false;
        if (prerelease && part.size() > 1 && part.front() == '0' && part.find_first_not_of("0123456789") == std::string_view::npos) return false;
        output.push_back(part);
        if (end == std::string_view::npos) return true;
        begin = end + 1;
    }
}
bool version(std::string_view value, Version& result) {
    const auto plus = value.find('+');
    if (plus != std::string_view::npos) {
        std::vector<std::string_view> build;
        if (!identifiers(value.substr(plus + 1), false, build)) return false;
        value = value.substr(0, plus);
    }
    const auto hyphen = value.find('-');
    if (hyphen != std::string_view::npos && !identifiers(value.substr(hyphen + 1), true, result.prerelease)) return false;
    const auto numeric = value.substr(0, hyphen);
    size_t begin = 0;
    for (size_t i = 0; i < result.numeric.size(); ++i) {
        const size_t end = i == 2 ? numeric.size() : numeric.find('.', begin);
        if (end == std::string_view::npos || end == begin) return false;
        const auto part = numeric.substr(begin, end - begin);
        if (part.size() > 1 && part.front() == '0') return false;
        auto parsed = std::from_chars(part.data(), part.data() + part.size(), result.numeric[i]);
        if (parsed.ec != std::errc{} || parsed.ptr != part.data() + part.size() || result.numeric[i] > 65535) return false;
        begin = end + 1;
    }
    return true;
}
}
bool newer_version(std::string_view candidate, std::string_view current) {
    Version left, right;
    if (!version(candidate, left) || !version(current, right)) return false;
    if (left.numeric != right.numeric) return left.numeric > right.numeric;
    if (left.prerelease.empty() || right.prerelease.empty()) return !right.prerelease.empty() && left.prerelease.empty();
    const auto count = (std::min)(left.prerelease.size(), right.prerelease.size());
    for (size_t i = 0; i < count; ++i) {
        const auto a = left.prerelease[i], b = right.prerelease[i];
        if (a == b) continue;
        const bool an = a.find_first_not_of("0123456789") == std::string_view::npos;
        const bool bn = b.find_first_not_of("0123456789") == std::string_view::npos;
        if (an != bn) return !an;
        if (an && a.size() != b.size()) return a.size() > b.size();
        return a > b;
    }
    return left.prerelease.size() > right.prerelease.size();
}
bool verify_update_manifest(std::string_view envelope, std::string_view expected_product, UpdateManifest& manifest, std::string& error) {
    manifest = {};
    constexpr std::string_view prefix = "FULLTOOLS-UPDATE-1\n";
    if (envelope.size() > 65536 || !envelope.starts_with(prefix)) { error = "Unsupported update envelope"; return false; }
    envelope.remove_prefix(prefix.size());
    const auto separator = envelope.find('\n');
    if (separator == std::string_view::npos) { error = "Missing update signature"; return false; }
    std::vector<std::uint8_t> body, signature;
    auto signature_text = envelope.substr(separator + 1);
    if (!signature_text.empty() && signature_text.back() == '\n') signature_text.remove_suffix(1);
    if (signature_text.find('\n') != std::string_view::npos || !decode(envelope.substr(0, separator), body) || !decode(signature_text, signature)) {
        error = "Invalid update encoding"; return false;
    }
    Algorithm algorithm;
    Key key;
    std::array<std::uint8_t, 32> hash{};
    if (!digest(body, hash) || BCryptOpenAlgorithmProvider(&algorithm.value, BCRYPT_RSA_ALGORITHM, nullptr, 0) != 0 ||
        BCryptImportKeyPair(algorithm.value, nullptr, BCRYPT_RSAPUBLIC_BLOB, &key.value, const_cast<PUCHAR>(update_public_key),
            static_cast<ULONG>(sizeof(update_public_key)), 0) != 0) { error = "Update verification unavailable"; return false; }
    BCRYPT_PSS_PADDING_INFO padding{BCRYPT_SHA256_ALGORITHM, 32};
    if (BCryptVerifySignature(key.value, &padding, hash.data(), static_cast<ULONG>(hash.size()), signature.data(),
            static_cast<ULONG>(signature.size()), BCRYPT_PAD_PSS) != 0) { error = "Update signature is invalid"; return false; }
    const std::string text(body.begin(), body.end());
    std::map<std::string, std::string> fields;
    size_t begin = 0;
    while (begin < text.size()) {
        const auto end = text.find('\n', begin);
        if (end == std::string::npos) { error = "Malformed update fields"; return false; }
        const auto line = text.substr(begin, end - begin); const auto equals = line.find('=');
        if (equals == std::string::npos || equals == 0 || !fields.emplace(line.substr(0, equals), line.substr(equals + 1)).second) {
            error = "Duplicate or malformed update field"; return false;
        }
        begin = end + 1;
    }
    if (fields.size() != 6) { error = "Unexpected update fields"; return false; }
    manifest.product = fields["product"]; manifest.version = fields["version"]; manifest.architecture = fields["architecture"];
    manifest.url = fields["url"]; manifest.sha256 = fields["sha256"];
    const auto& size = fields["bytes"];
    const auto parsed = std::from_chars(size.data(), size.data() + size.size(), manifest.bytes);
    Version numeric;
    if (manifest.product != expected_product || manifest.architecture != "windows-x64" || !version(manifest.version, numeric) ||
        !manifest.url.starts_with("https://") || manifest.url.find_first_of("\r\n\0", 0, 3) != std::string::npos ||
        manifest.sha256.size() != 64 || manifest.sha256.find_first_not_of("0123456789abcdef") != std::string::npos ||
        parsed.ec != std::errc{} || parsed.ptr != size.data() + size.size() || manifest.bytes == 0 || manifest.bytes > 2ULL * 1024 * 1024 * 1024) {
        error = "Update product, platform, version, or download contract is invalid"; return false;
    }
    return true;
}
}
