"""Collect reproducible, read-only evidence from three explicit Antigravity files.

The collector never launches vendor code, enumerates profile data, calls a
credential API, or emits source lines or sensitive values recovered from file
contents. Normal reports do include the normalized paths of the three inputs
explicitly supplied by the caller.

Usage:
  python scripts/Collect-AntigravityStaticEvidence.py \
    --antigravity-exe path/to/Antigravity.exe \
    --app-asar path/to/app.asar \
    --language-server-exe path/to/language_server.exe \
    [--output evidence.json]

  python scripts/Collect-AntigravityStaticEvidence.py --self-test
"""

import argparse
import bisect
import hashlib
import json
import os
import re
import struct
import sys
import tempfile
from pathlib import Path


SCHEMA_VERSION = 1
COLLECTOR_VERSION = "1.0.0"
MAX_ASAR_HEADER_BYTES = 64 * 1024 * 1024
SCAN_CHUNK_BYTES = 4 * 1024 * 1024

RELEVANT_ASAR_MEMBERS = {
    "package.json",
    "dist/constants.js",
    "dist/hostBridgeServer.js",
    "dist/ipcHandlers.js",
    "dist/languageServer.js",
    "dist/main.js",
    "dist/paths.js",
    "dist/utils.js",
}

KNOWN_LS_ARGUMENTS = (
    "--api_server_url",
    "--app_data_dir",
    "--cloud_code_endpoint",
    "--csrf_token",
    "--enable_sidecars",
    "--headless",
    "--host_bridge_token",
    "--host_bridge_url",
    "--https_server_port",
    "--override_ide_name",
    "--override_ide_version",
    "--override_user_agent_name",
    "--stamp",
    "--standalone",
    "--subclient_type",
)

SENSITIVE_VALUE_ARGUMENTS = {
    "--csrf_token",
    "--host_bridge_token",
}

BINARY_MARKERS = {
    "oauth.googleAuthorizationEndpoint": b"accounts.google.com/o/oauth2/auth",
    "oauth.googleTokenEndpoint": b"oauth2.googleapis.com/token",
    "oauth.googleDeviceCodeEndpoint": b"oauth2.googleapis.com/device/code",
    "oauth.antigravityCallbackEndpoint": b"antigravity.google/oauth-callback",
    "oauth.antigravityProtocol": b"antigravity://",
    "oauth.loopbackAuthCallbackTemplate": b"localhost:%d/auth/callback",
    "oauth.genericLoopbackCallbackTemplate": b"http://%s/callback",
    "credentials.wincred": b"wincred",
    "credentials.credReadW": b"CredReadW",
    "credentials.credWriteW": b"CredWriteW",
    "credentials.credDeleteW": b"CredDeleteW",
    "credentials.credEnumerateW": b"CredEnumerateW",
    "credentials.goKeyring": b"go-keyring",
    "credentials.keyring": b"keyring",
    "agy.agyNode": b"agy-node",
    "agy.agyNodeExe": b"agy-node.exe",
    "agy.agyPathSegment": b".agy",
    "agy.agySubstring": b"agy",
    "agentApi.getConversationMetadata": b"get-conversation-metadata",
    "agentApi.newConversation": b"new-conversation",
    "agentApi.sendMessage": b"send-message",
    "agentApi.csrfUnaryInterceptor": b"csrfUnaryInterceptor",
    "agentApi.addressEnvironmentName": b"ANTIGRAVITY_LS_ADDRESS",
    "agentApi.csrfEnvironmentName": b"ANTIGRAVITY_CSRF_TOKEN",
    "agentApi.missingAddressDiagnostic": b"ANTIGRAVITY_LS_ADDRESS is not set",
    "agentApi.notSetDiagnosticFragment": b"is not set",
    "agentApi.commandPath": b"cmd/agentapi",
}

SOURCE_FACT_RULES = (
    {
        "id": "package-main-entrypoint",
        "category": "startup",
        "member": "package.json",
        "regex": r'(?m)^\s*"main"\s*:',
        "statement": "Package metadata declares the Electron main entry point.",
    },
    {
        "id": "electron-single-instance-lock",
        "category": "startup",
        "member": "dist/main.js",
        "needle": "requestSingleInstanceLock",
        "statement": "The Electron main process requests the application singleton lock.",
    },
    {
        "id": "electron-ready-startup",
        "category": "startup",
        "member": "dist/main.js",
        "needle": "whenReady",
        "statement": "Main startup is attached to Electron's ready lifecycle.",
    },
    {
        "id": "electron-protocol-registration",
        "category": "oauth",
        "member": "dist/main.js",
        "needle": "setAsDefaultProtocolClient",
        "statement": "The Electron main process registers a default protocol client.",
    },
    {
        "id": "main-starts-host-bridge",
        "category": "ipc",
        "member": "dist/main.js",
        "regex": r"startHostBridgeServer\)?\s*\(",
        "statement": "The Electron main process invokes host-bridge startup.",
    },
    {
        "id": "main-starts-language-server",
        "category": "startup",
        "member": "dist/main.js",
        "regex": r"startAndMonitorLanguageServer\)?\s*\(",
        "statement": "The Electron main process invokes language-server startup and monitoring.",
    },
    {
        "id": "language-server-image-name",
        "category": "startup",
        "member": "dist/constants.js",
        "needle": "language_server",
        "statement": "The package contains the configured language-server image name.",
    },
    {
        "id": "language-server-child-spawn",
        "category": "startup",
        "member": "dist/languageServer.js",
        "needle": "child_process_1.spawn",
        "statement": "The language-server launcher invokes the Node child-process spawn API.",
    },
    {
        "id": "language-server-https-port-argument",
        "category": "port",
        "member": "dist/languageServer.js",
        "needle": "--https_server_port",
        "statement": "The launcher passes a local HTTPS server port argument to the language server.",
    },
    {
        "id": "language-server-csrf-argument",
        "category": "ipc",
        "member": "dist/languageServer.js",
        "needle": "--csrf_token",
        "statement": "The launcher passes a CSRF argument by name; its value is not collected.",
    },
    {
        "id": "language-server-app-data-argument",
        "category": "file",
        "member": "dist/languageServer.js",
        "needle": "--app_data_dir",
        "statement": "The launcher passes an application-data directory argument to the language server.",
    },
    {
        "id": "language-server-host-bridge-url",
        "category": "ipc",
        "member": "dist/languageServer.js",
        "needle": "--host_bridge_url",
        "statement": "The launcher passes a host-bridge URL argument to the language server.",
    },
    {
        "id": "language-server-host-bridge-token",
        "category": "ipc",
        "member": "dist/languageServer.js",
        "needle": "--host_bridge_token",
        "statement": "The launcher passes a host-bridge token argument by name; its value is not collected.",
    },
    {
        "id": "electron-user-data-path",
        "category": "file",
        "member": "dist/paths.js",
        "needle": "getPath('userData')",
        "statement": "The packaged path logic references Electron's user-data directory.",
    },
    {
        "id": "process-home-gemini-path",
        "category": "file",
        "member": "dist/paths.js",
        "needle": ".gemini",
        "statement": "The packaged path logic references a .gemini path segment under process-home logic.",
    },
    {
        "id": "agy-data-path",
        "category": "agy",
        "member": "dist/paths.js",
        "needle": "AGY",
        "statement": "The packaged path logic references an AGY-named path value.",
    },
    {
        "id": "agy-node-image-reference",
        "category": "agy",
        "member": "dist/utils.js",
        "needle": "agy-node",
        "statement": "The package resolves a bundled agy-node image or wrapper.",
    },
    {
        "id": "agy-node-child-spawn",
        "category": "agy",
        "member": "dist/utils.js",
        "regex": r"\bspawn\b",
        "statement": "The agy-node utility module also contains a child-process spawn marker; target linkage is not proven.",
    },
)


class EvidenceError(Exception):
    pass


def _stat_fingerprint(path):
    try:
        stat = path.stat()
    except OSError as error:
        raise EvidenceError("Could not stat required input: " + path.name) from error
    return (stat.st_size, stat.st_mtime_ns, stat.st_ino)


def _file_identity(path):
    try:
        stat = path.stat()
    except OSError as error:
        raise EvidenceError("Could not identify evidence output path.") from error
    return (stat.st_dev, stat.st_ino)


def _sha256_file(path):
    before = _stat_fingerprint(path)
    digest = hashlib.sha256()
    try:
        with path.open("rb") as stream:
            for chunk in iter(lambda: stream.read(SCAN_CHUNK_BYTES), b""):
                digest.update(chunk)
    except OSError as error:
        raise EvidenceError("Could not read required input: " + path.name) from error
    if _stat_fingerprint(path) != before:
        raise EvidenceError("Required input changed while it was being hashed: " + path.name)
    return digest.hexdigest()


def _validate_input(path_text, expected_name):
    supplied = Path(path_text)
    if supplied.name.casefold() != expected_name.casefold():
        raise EvidenceError("Expected an explicitly named " + expected_name + " input.")
    try:
        resolved = supplied.resolve(strict=True)
    except OSError as error:
        raise EvidenceError("Required input does not exist: " + expected_name) from error
    if resolved.name.casefold() != expected_name.casefold() or not resolved.is_file():
        raise EvidenceError("Required input is not a regular " + expected_name + " file.")
    return resolved


def _validate_output_target(output_path, protected_paths):
    output_path = Path(output_path)
    try:
        resolved_output = output_path.resolve(strict=False)
        output_entry_exists = os.path.lexists(output_path)
        for protected_path in protected_paths:
            if resolved_output == protected_path:
                raise EvidenceError("Evidence output must not overwrite an input or the collector.")
            if output_entry_exists and os.path.samefile(output_path, protected_path):
                raise EvidenceError("Evidence output must not alias an input or the collector.")
        if output_entry_exists:
            raise EvidenceError("Evidence output already exists; refusing to overwrite it.")
    except EvidenceError:
        raise
    except OSError as error:
        raise EvidenceError("Could not validate the requested evidence output path.") from error
    return resolved_output


def _write_output_atomically(output_path, output_text, protected_paths):
    resolved_output = _validate_output_target(output_path, protected_paths)
    temporary_path = None
    temporary_identity = None
    reservation_identity = None
    descriptor = None
    reservation_descriptor = None
    try:
        descriptor, temporary_name = tempfile.mkstemp(
            prefix="." + resolved_output.name + ".",
            suffix=".tmp",
            dir=str(resolved_output.parent),
        )
        temporary_path = Path(temporary_name)
        temporary_stat = os.fstat(descriptor)
        temporary_identity = (temporary_stat.st_dev, temporary_stat.st_ino)
        with os.fdopen(descriptor, "w", encoding="utf-8", newline="\n") as output_stream:
            descriptor = None
            output_stream.write(output_text)
            output_stream.flush()
            os.fsync(output_stream.fileno())
        final_output = _validate_output_target(resolved_output, protected_paths)
        flags = os.O_CREAT | os.O_EXCL | os.O_WRONLY
        if hasattr(os, "O_BINARY"):
            flags |= os.O_BINARY
        reservation_descriptor = os.open(final_output, flags, 0o600)
        reservation_stat = os.fstat(reservation_descriptor)
        reservation_identity = (reservation_stat.st_dev, reservation_stat.st_ino)
        os.close(reservation_descriptor)
        reservation_descriptor = None
        if final_output.resolve(strict=True) != resolved_output:
            raise EvidenceError("Evidence output path changed during final validation.")
        if _file_identity(final_output) != reservation_identity:
            raise EvidenceError("Evidence output reservation changed before publication.")

        os.replace(temporary_path, final_output)
        temporary_path = None
        temporary_identity = None
        reservation_identity = None
    except EvidenceError:
        raise
    except OSError as error:
        raise EvidenceError("Could not atomically publish the requested evidence output file.") from error
    finally:
        if descriptor is not None:
            try:
                os.close(descriptor)
            except OSError:
                pass
        if reservation_descriptor is not None:
            try:
                os.close(reservation_descriptor)
            except OSError:
                pass
        if temporary_path is not None and temporary_identity is not None:
            try:
                if _file_identity(temporary_path) == temporary_identity:
                    temporary_path.unlink()
            except (EvidenceError, OSError):
                pass
        if reservation_identity is not None:
            try:
                if _file_identity(resolved_output) == reservation_identity:
                    resolved_output.unlink()
            except (EvidenceError, OSError):
                pass


def _fixed_file_version(path):
    result = {
        "method": "Windows VS_FIXEDFILEINFO resource (read-only)",
        "fileVersion": None,
        "productVersion": None,
    }
    if os.name != "nt":
        return result
    try:
        import ctypes
        from ctypes import wintypes

        class FixedFileInfo(ctypes.Structure):
            _fields_ = [
                ("signature", wintypes.DWORD),
                ("structVersion", wintypes.DWORD),
                ("fileVersionMS", wintypes.DWORD),
                ("fileVersionLS", wintypes.DWORD),
                ("productVersionMS", wintypes.DWORD),
                ("productVersionLS", wintypes.DWORD),
                ("fileFlagsMask", wintypes.DWORD),
                ("fileFlags", wintypes.DWORD),
                ("fileOS", wintypes.DWORD),
                ("fileType", wintypes.DWORD),
                ("fileSubtype", wintypes.DWORD),
                ("fileDateMS", wintypes.DWORD),
                ("fileDateLS", wintypes.DWORD),
            ]

        version_dll = ctypes.WinDLL("version", use_last_error=True)
        get_size = version_dll.GetFileVersionInfoSizeW
        get_size.argtypes = [wintypes.LPCWSTR, ctypes.POINTER(wintypes.DWORD)]
        get_size.restype = wintypes.DWORD
        get_info = version_dll.GetFileVersionInfoW
        get_info.argtypes = [wintypes.LPCWSTR, wintypes.DWORD, wintypes.DWORD, wintypes.LPVOID]
        get_info.restype = wintypes.BOOL
        query = version_dll.VerQueryValueW
        query.argtypes = [wintypes.LPCVOID, wintypes.LPCWSTR, ctypes.POINTER(ctypes.c_void_p), ctypes.POINTER(wintypes.UINT)]
        query.restype = wintypes.BOOL

        ignored_handle = wintypes.DWORD(0)
        size = get_size(str(path), ctypes.byref(ignored_handle))
        if not size:
            return result
        buffer = ctypes.create_string_buffer(size)
        if not get_info(str(path), 0, size, buffer):
            return result
        value_pointer = ctypes.c_void_p()
        value_length = wintypes.UINT(0)
        if not query(buffer, "\\", ctypes.byref(value_pointer), ctypes.byref(value_length)):
            return result
        if value_length.value < ctypes.sizeof(FixedFileInfo):
            return result
        info = ctypes.cast(value_pointer, ctypes.POINTER(FixedFileInfo)).contents
        if info.signature != 0xFEEF04BD:
            return result

        def format_version(ms_value, ls_value):
            return ".".join(
                str(part)
                for part in (
                    ms_value >> 16,
                    ms_value & 0xFFFF,
                    ls_value >> 16,
                    ls_value & 0xFFFF,
                )
            )

        result["fileVersion"] = format_version(info.fileVersionMS, info.fileVersionLS)
        result["productVersion"] = format_version(info.productVersionMS, info.productVersionLS)
    except (AttributeError, OSError, ValueError):
        return result
    return result


def _parse_asar(path):
    try:
        archive_fingerprint = _stat_fingerprint(path)
        archive_size = archive_fingerprint[0]
        archive_sha256 = _sha256_file(path)
        with path.open("rb") as stream:
            prefix = stream.read(16)
            if len(prefix) != 16:
                raise EvidenceError("app.asar has an incomplete header.")
            pickle_size, header_size, string_size, json_size = struct.unpack("<4I", prefix)
            if pickle_size != 4 or header_size != json_size + 8 or string_size != json_size + 4:
                raise EvidenceError("app.asar has an unsupported header layout.")
            if json_size > MAX_ASAR_HEADER_BYTES:
                raise EvidenceError("app.asar header exceeds the inspection limit.")
            payload_offset = 8 + header_size
            if payload_offset > archive_size:
                raise EvidenceError("app.asar payload offset is outside the archive.")
            header_bytes = stream.read(json_size)
            if len(header_bytes) != json_size:
                raise EvidenceError("app.asar has a truncated header.")
            try:
                header = json.loads(header_bytes.decode("utf-8"))
            except (UnicodeDecodeError, json.JSONDecodeError) as error:
                raise EvidenceError("app.asar header is not valid UTF-8 JSON.") from error
            if not isinstance(header, dict) or not isinstance(header.get("files"), dict):
                raise EvidenceError("app.asar header has no file manifest.")

            manifest_entries = []

            def walk(node, prefix_path="", depth=0):
                if depth > 256:
                    raise EvidenceError("app.asar manifest nesting exceeds the inspection limit.")
                files = node.get("files")
                if not isinstance(files, dict):
                    raise EvidenceError("app.asar contains an invalid directory entry.")
                for name, child in files.items():
                    if (
                        not isinstance(name, str)
                        or not name
                        or name in (".", "..")
                        or "/" in name
                        or "\\" in name
                        or "\x00" in name
                        or not isinstance(child, dict)
                    ):
                        raise EvidenceError("app.asar contains an unsafe manifest entry.")
                    member_path = prefix_path + "/" + name if prefix_path else name
                    if "files" in child:
                        walk(child, member_path, depth + 1)
                    elif "link" in child:
                        manifest_entries.append((member_path, child, "link"))
                    elif child.get("unpacked") is True:
                        manifest_entries.append((member_path, child, "unpacked"))
                    else:
                        manifest_entries.append((member_path, child, "packed"))

            walk(header)
            manifest_entries.sort(key=lambda entry: entry[0])
            inventory = []
            relevant_content = {}
            packed_count = 0
            unpacked_count = 0
            link_count = 0
            text_count = 0

            for member_path, node, storage in manifest_entries:
                size = node.get("size")
                if storage == "link":
                    link_count += 1
                    inventory.append(
                        {
                            "path": member_path,
                            "storage": "link-manifest-only",
                            "sizeBytes": None,
                            "sha256": None,
                            "lineCount": None,
                            "contentClassification": "not-read",
                        }
                    )
                    continue
                if isinstance(size, bool) or not isinstance(size, int) or size < 0:
                    raise EvidenceError("app.asar contains an invalid member size.")
                if storage == "unpacked":
                    unpacked_count += 1
                    inventory.append(
                        {
                            "path": member_path,
                            "storage": "external-unpacked-not-read",
                            "sizeBytes": size,
                            "sha256": None,
                            "lineCount": None,
                            "contentClassification": "not-read",
                        }
                    )
                    continue
                offset_value = node.get("offset", "0" if size == 0 else None)
                if isinstance(offset_value, bool) or offset_value is None:
                    raise EvidenceError("app.asar contains an invalid packed-member offset.")
                offset_text = str(offset_value)
                if not offset_text.isascii() or not offset_text.isdigit():
                    raise EvidenceError("app.asar contains an invalid packed-member offset.")
                offset = int(offset_text)
                absolute_offset = payload_offset + offset
                if absolute_offset < payload_offset or absolute_offset + size > archive_size:
                    raise EvidenceError("app.asar member range is outside the archive.")
                stream.seek(absolute_offset)
                content = stream.read(size)
                if len(content) != size:
                    raise EvidenceError("app.asar contains a truncated member.")
                member_sha256 = hashlib.sha256(content).hexdigest()
                line_count = None
                classification = "binary-or-non-UTF8"
                if b"\x00" not in content:
                    try:
                        content.decode("utf-8")
                        line_count = 0 if not content else content.count(b"\n") + (0 if content.endswith(b"\n") else 1)
                        classification = "utf8-text"
                        text_count += 1
                    except UnicodeDecodeError:
                        pass
                packed_count += 1
                inventory.append(
                    {
                        "path": member_path,
                        "storage": "packed",
                        "sizeBytes": size,
                        "sha256": member_sha256,
                        "lineCount": line_count,
                        "contentClassification": classification,
                    }
                )
                if member_path in RELEVANT_ASAR_MEMBERS:
                    relevant_content[member_path] = content

            if _stat_fingerprint(path) != archive_fingerprint:
                raise EvidenceError("app.asar changed while it was being inspected.")

    except OSError as error:
        raise EvidenceError("Could not inspect app.asar.") from error

    inventory_digest = hashlib.sha256(
        json.dumps(inventory, ensure_ascii=False, sort_keys=True, separators=(",", ":")).encode("utf-8")
    ).hexdigest()
    report = {
        "sha256": archive_sha256,
        "sizeBytes": archive_size,
        "headerSha256": hashlib.sha256(header_bytes).hexdigest(),
        "payloadOffsetBytes": payload_offset,
        "memberInventorySha256": inventory_digest,
        "memberCounts": {
            "total": len(inventory),
            "packedHashed": packed_count,
            "packedUtf8TextWithLineCount": text_count,
            "externalUnpackedNotRead": unpacked_count,
            "linksNotFollowed": link_count,
        },
        "lineCountAlgorithm": "LF-delimited UTF-8 lines; a non-empty final segment counts as one line",
        "members": inventory,
    }
    return report, relevant_content


def _decode_relevant_sources(relevant_content):
    decoded = {}
    decode_failures = set()
    for member, content in relevant_content.items():
        try:
            decoded[member] = content.decode("utf-8")
        except UnicodeDecodeError:
            decode_failures.add(member)
    return decoded, decode_failures


def _line_locations(text, needle=None, regex=None):
    if regex is not None:
        offsets = [match.start() for match in re.finditer(regex, text)]
    else:
        offsets = [match.start() for match in re.finditer(re.escape(needle), text)]
    newline_offsets = [index for index, character in enumerate(text) if character == "\n"]
    line_numbers = sorted({bisect.bisect_left(newline_offsets, offset) + 1 for offset in offsets})
    lines = text.split("\n")
    locations = []
    for line_number in line_numbers:
        line = lines[line_number - 1]
        if line.endswith("\r"):
            line = line[:-1]
        locations.append(
            {
                "line": line_number,
                "lineSha256": hashlib.sha256(line.encode("utf-8")).hexdigest(),
            }
        )
    return len(offsets), locations


def _build_source_facts(sources, member_index, decode_failures):
    facts = []
    for rule in SOURCE_FACT_RULES:
        text = sources.get(rule["member"])
        occurrence_count = 0
        locations = []
        inspection_reason = None
        if text is not None:
            occurrence_count, locations = _line_locations(
                text,
                needle=rule.get("needle"),
                regex=rule.get("regex"),
            )
        member = member_index.get(rule["member"], {})
        if text is None:
            storage = member.get("storage")
            if rule["member"] in decode_failures:
                inspection_reason = "utf8-decode-failed"
            elif storage == "external-unpacked-not-read":
                inspection_reason = "external-unpacked-member-not-read"
            elif storage == "link-manifest-only":
                inspection_reason = "link-member-not-followed"
            elif not member:
                inspection_reason = "member-not-in-manifest"
            else:
                inspection_reason = "member-content-not-available"
        status = "not-inspected" if inspection_reason else ("present" if occurrence_count else "absent")
        facts.append(
            {
                "id": rule["id"],
                "category": rule["category"],
                "statement": rule["statement"],
                "status": status,
                "evidenceLevel": "static-source-location",
                "source": {
                    "member": rule["member"],
                    "memberSha256": member.get("sha256"),
                    "memberLineCount": member.get("lineCount"),
                    "inspectionStatus": "not-inspected" if inspection_reason else "inspected",
                    "inspectionReason": inspection_reason,
                    "occurrenceCount": occurrence_count,
                    "locations": locations,
                },
            }
        )
    return facts


def _extract_help_arguments(sources, member_index):
    member_name = "dist/languageServer.js"
    text = sources.get(member_name, "")
    member = member_index.get(member_name, {})
    arguments = []
    for argument in KNOWN_LS_ARGUMENTS:
        occurrence_count, locations = _line_locations(text, needle=argument)
        if not occurrence_count:
            continue
        arguments.append(
            {
                "name": argument,
                "sensitiveValue": argument in SENSITIVE_VALUE_ARGUMENTS,
                "valueCollected": False,
                "source": {
                    "member": member_name,
                    "memberSha256": member.get("sha256"),
                    "occurrenceCount": occurrence_count,
                    "locations": locations,
                },
            }
        )
    return arguments


def _scan_binary(path, marker_patterns):
    binary_fingerprint = _stat_fingerprint(path)
    encoded_patterns = {}
    for marker_id, pattern in marker_patterns.items():
        encoded_patterns[marker_id] = {
            "ascii": pattern.lower(),
            "utf16le": pattern.decode("ascii").encode("utf-16le").lower(),
        }
    maximum_pattern_size = max(
        len(pattern)
        for encodings in encoded_patterns.values()
        for pattern in encodings.values()
    )
    counts = {
        marker_id: {"ascii": 0, "utf16le": 0}
        for marker_id in encoded_patterns
    }
    digest = hashlib.sha256()
    carry = b""

    def scan_prefix(data, start_limit):
        lowered = data.lower()
        for marker_id, encodings in encoded_patterns.items():
            for encoding, pattern in encodings.items():
                start = 0
                while True:
                    position = lowered.find(pattern, start)
                    if position < 0 or position >= start_limit:
                        break
                    counts[marker_id][encoding] += 1
                    start = position + 1

    try:
        with path.open("rb") as stream:
            while True:
                chunk = stream.read(SCAN_CHUNK_BYTES)
                if not chunk:
                    scan_prefix(carry, len(carry))
                    break
                digest.update(chunk)
                data = carry + chunk
                safe_prefix = max(0, len(data) - maximum_pattern_size + 1)
                scan_prefix(data, safe_prefix)
                carry = data[safe_prefix:]
    except OSError as error:
        raise EvidenceError("Could not inspect language_server.exe.") from error
    if _stat_fingerprint(path) != binary_fingerprint:
        raise EvidenceError("language_server.exe changed while it was being inspected.")

    marker_report = {}
    for marker_id, encoding_counts in counts.items():
        marker_report[marker_id] = {
            "asciiCount": encoding_counts["ascii"],
            "utf16leCount": encoding_counts["utf16le"],
            "totalCount": encoding_counts["ascii"] + encoding_counts["utf16le"],
            "evidenceLevel": "static-binary-marker-count",
        }
    return {
        "sha256": digest.hexdigest(),
        "sizeBytes": binary_fingerprint[0],
        "markers": marker_report,
    }


def _package_metadata(sources):
    metadata = {
        "nameIsAntigravity": False,
        "version": None,
        "mainEntrypoint": None,
    }
    package_text = sources.get("package.json")
    if package_text is None:
        return metadata
    try:
        package = json.loads(package_text)
    except json.JSONDecodeError:
        return metadata
    if not isinstance(package, dict):
        return metadata
    metadata["nameIsAntigravity"] = package.get("name") == "antigravity"
    version = package.get("version")
    if isinstance(version, str) and re.fullmatch(r"[0-9A-Za-z][0-9A-Za-z._+-]{0,63}", version):
        metadata["version"] = version
    entrypoint = package.get("main")
    if isinstance(entrypoint, str) and re.fullmatch(r"[0-9A-Za-z_./-]{1,128}", entrypoint):
        metadata["mainEntrypoint"] = entrypoint
    return metadata


def _group_binary_markers(binary_markers, prefix):
    return {
        marker_id.split(".", 1)[1]: marker
        for marker_id, marker in binary_markers.items()
        if marker_id.startswith(prefix + ".")
    }


def _build_graph(facts, oauth_markers, agent_api_markers):
    present_facts = {fact["id"] for fact in facts if fact["status"] == "present"}
    nodes = [
        {"id": "antigravity-exe", "kind": "process-image"},
        {"id": "app-asar", "kind": "package"},
        {"id": "electron-main", "kind": "runtime-role"},
        {"id": "host-bridge", "kind": "local-ipc-role"},
        {"id": "language-server-exe", "kind": "process-image"},
        {"id": "language-server-local-https", "kind": "local-port-role"},
        {"id": "electron-user-data", "kind": "filesystem-root-reference"},
        {"id": "process-home-gemini", "kind": "filesystem-root-reference"},
        {"id": "agy-data", "kind": "filesystem-path-reference"},
        {"id": "agy-node", "kind": "process-image-reference"},
        {"id": "oauth-provider", "kind": "remote-endpoint-role"},
        {"id": "oauth-callback", "kind": "callback-role"},
        {"id": "agentapi-cli", "kind": "language-server-multicall-role"},
    ]
    edges = []

    def add_edge(source, target, relation, evidence_ids, evidence_level="direct-static"):
        if all(evidence_id in present_facts for evidence_id in evidence_ids):
            edges.append(
                {
                    "from": source,
                    "to": target,
                    "relation": relation,
                    "evidence": [{"sourceFactId": evidence_id} for evidence_id in evidence_ids],
                    "evidenceLevel": evidence_level,
                }
            )

    add_edge("app-asar", "electron-main", "declares-entrypoint", ["package-main-entrypoint"])
    add_edge("electron-main", "host-bridge", "starts", ["main-starts-host-bridge"])
    add_edge(
        "electron-main",
        "language-server-exe",
        "starts-and-monitors",
        ["main-starts-language-server", "language-server-child-spawn"],
        "correlated",
    )
    add_edge(
        "electron-main",
        "language-server-local-https",
        "assigns-port-and-csrf-argument-names",
        ["language-server-https-port-argument", "language-server-csrf-argument"],
    )
    add_edge(
        "language-server-exe",
        "host-bridge",
        "receives-url-and-token-argument-names",
        ["language-server-host-bridge-url", "language-server-host-bridge-token"],
    )
    add_edge("electron-main", "electron-user-data", "resolves-path", ["electron-user-data-path"])
    add_edge("electron-main", "process-home-gemini", "resolves-path", ["process-home-gemini-path"])
    add_edge("electron-main", "agy-data", "references-path", ["agy-data-path"])
    add_edge(
        "electron-main",
        "agy-node",
        "co-occurs-with-spawn-marker-in-utility",
        ["agy-node-image-reference", "agy-node-child-spawn"],
        "correlated",
    )

    authorization_present = oauth_markers.get("googleAuthorizationEndpoint", {}).get("totalCount", 0) > 0
    token_present = oauth_markers.get("googleTokenEndpoint", {}).get("totalCount", 0) > 0
    device_code_present = oauth_markers.get("googleDeviceCodeEndpoint", {}).get("totalCount", 0) > 0
    if authorization_present or token_present or device_code_present:
        marker_names = []
        if authorization_present:
            marker_names.append("oauth.googleAuthorizationEndpoint")
        if token_present:
            marker_names.append("oauth.googleTokenEndpoint")
        if device_code_present:
            marker_names.append("oauth.googleDeviceCodeEndpoint")
        edges.append(
            {
                "from": "language-server-exe",
                "to": "oauth-provider",
                "relation": "contains-endpoint-markers",
                "evidence": [{"binaryMarkerId": marker_name} for marker_name in marker_names],
                "evidenceLevel": "direct-static",
            }
        )
    callback_names = [
        name
        for name in (
            "antigravityCallbackEndpoint",
            "loopbackAuthCallbackTemplate",
            "genericLoopbackCallbackTemplate",
        )
        if oauth_markers.get(name, {}).get("totalCount", 0) > 0
    ]
    if callback_names:
        edges.append(
            {
                "from": "language-server-exe",
                "to": "oauth-callback",
                "relation": "contains-callback-template-markers",
                "evidence": [{"binaryMarkerId": "oauth." + name} for name in callback_names],
                "evidenceLevel": "direct-static",
            }
        )
    antigravity_protocol_present = oauth_markers.get("antigravityProtocol", {}).get("totalCount", 0) > 0
    if antigravity_protocol_present:
        edges.append(
            {
                "from": "language-server-exe",
                "to": "oauth-callback",
                "relation": "contains-custom-protocol-marker",
                "evidence": [{"binaryMarkerId": "oauth.antigravityProtocol"}],
                "evidenceLevel": "direct-static",
            }
        )
    agent_api_commands = [
        name
        for name in ("getConversationMetadata", "newConversation", "sendMessage")
        if agent_api_markers.get(name, {}).get("totalCount", 0) > 0
    ]
    if agent_api_commands:
        edges.append(
            {
                "from": "language-server-exe",
                "to": "agentapi-cli",
                "relation": "contains-public-subcommand-markers",
                "evidence": [
                    {"binaryMarkerId": "agentApi." + name}
                    for name in agent_api_commands
                ],
                "evidenceLevel": "direct-static",
            }
        )
    return {
        "evidenceScope": "static-only",
        "edgeEvidenceLevels": ["direct-static", "correlated", "hypothesized"],
        "runtimeObservation": False,
        "nodes": nodes,
        "edges": edges,
    }


def collect(antigravity_exe, app_asar, language_server_exe):
    input_snapshots = (
        ("Antigravity.exe", antigravity_exe, _stat_fingerprint(antigravity_exe)),
        ("app.asar", app_asar, _stat_fingerprint(app_asar)),
        ("language_server.exe", language_server_exe, _stat_fingerprint(language_server_exe)),
    )
    input_sizes = {
        input_name: fingerprint[0]
        for input_name, _, fingerprint in input_snapshots
    }
    asar_report, relevant_content = _parse_asar(app_asar)
    sources, decode_failures = _decode_relevant_sources(relevant_content)
    member_index = {member["path"]: member for member in asar_report["members"]}
    package = _package_metadata(sources)
    facts = _build_source_facts(sources, member_index, decode_failures)
    help_arguments = _extract_help_arguments(sources, member_index)

    marker_patterns = dict(BINARY_MARKERS)
    for argument in help_arguments:
        argument_name = argument["name"]
        marker_patterns["lsArgument.literal." + argument_name[2:]] = argument_name.encode("ascii")
        marker_patterns["lsArgument.identifier." + argument_name[2:]] = argument_name[2:].encode("ascii")
    language_server_scan = _scan_binary(language_server_exe, marker_patterns)
    binary_markers = language_server_scan.pop("markers")

    for argument in help_arguments:
        safe_name = argument["name"][2:]
        argument["binaryEvidence"] = {
            "literalWithDashes": binary_markers.pop("lsArgument.literal." + safe_name),
            "identifierWithoutDashes": binary_markers.pop("lsArgument.identifier." + safe_name),
        }

    oauth_markers = _group_binary_markers(binary_markers, "oauth")
    credential_markers = _group_binary_markers(binary_markers, "credentials")
    agy_markers = _group_binary_markers(binary_markers, "agy")
    agent_api_markers = _group_binary_markers(binary_markers, "agentApi")

    report = {
        "schemaVersion": SCHEMA_VERSION,
        "evidenceLevel": "static-read-only",
        "collector": {
            "name": "Collect-AntigravityStaticEvidence.py",
            "version": COLLECTOR_VERSION,
            "deterministicOutput": True,
            "runtimeTimestampIncluded": False,
        },
        "collectionPolicy": {
            "explicitInputFilesOnly": True,
            "inputFileNames": ["Antigravity.exe", "app.asar", "language_server.exe"],
            "normalizedExplicitInputPathsEmitted": True,
            "inputSnapshotConsistencyVerified": True,
            "vendorProvenanceVerified": False,
            "crossInputCohortVerified": False,
            "directoryEnumeration": False,
            "externalAsarUnpackedMembersRead": False,
            "userProfileDiscovery": False,
            "credentialApiCalls": False,
            "vendorProcessExecution": False,
            "languageServerHelpExecuted": False,
            "vendorSourceCodeEmitted": False,
            "tokenOrClientSecretValuesRecoveredFromInputContentsEmitted": False,
        },
        "evidenceLevelDefinitions": {
            "static-read-only": "Bytes and metadata read only from explicitly supplied files.",
            "static-source-location": "A fixed safe marker is present at a hashed ASAR member line; no line body is emitted.",
            "static-binary-marker-count": "A fixed safe ASCII or UTF-16LE marker count in a binary; no surrounding bytes are emitted.",
            "direct-static": "The stated relationship is directly limited to what a cited static marker establishes.",
            "correlated": "Separate cited static markers support an association but do not prove runtime linkage or causality.",
            "hypothesized": "Static clues suggest the relationship, but direct or correlated evidence is not sufficient.",
        },
        "inputs": {
            "antigravityExecutable": {
                "path": str(antigravity_exe),
                "sizeBytes": input_sizes["Antigravity.exe"],
                "sha256": _sha256_file(antigravity_exe),
                "version": _fixed_file_version(antigravity_exe),
            },
            "appAsar": {
                "path": str(app_asar),
                "sizeBytes": asar_report["sizeBytes"],
                "sha256": asar_report["sha256"],
                "package": package,
            },
            "languageServerExecutable": {
                "path": str(language_server_exe),
                "sizeBytes": language_server_scan["sizeBytes"],
                "sha256": language_server_scan["sha256"],
                "version": _fixed_file_version(language_server_exe),
            },
        },
        "asar": asar_report,
        "startupChainFacts": facts,
        "languageServerHelp": {
            "evidenceLevel": "static-source-location",
            "helpExecuted": False,
            "extractionMethod": "fixed safe-name recovery from the explicitly supplied packaged launcher, cross-counted in the LS binary",
            "scope": "Launcher-observed public argument names; not an exhaustive vendor help transcript.",
            "argumentValuesCollected": False,
            "publicArguments": help_arguments,
        },
        "oauth": {
            "evidenceLevel": "static-binary-marker-count",
            "endpointOrCallbackValuesEmitted": False,
            "fixedMarkerSetExhaustive": False,
            "markers": oauth_markers,
            "customProtocolRegistrationFactId": "electron-protocol-registration",
        },
        "credentialLibraries": {
            "evidenceLevel": "static-binary-marker-count",
            "credentialValuesRead": False,
            "countsAreRawSubstringMarkersNotRuntimeCalls": True,
            "markers": credential_markers,
        },
        "agy": {
            "evidenceLevel": "static-binary-marker-count",
            "countsAreRawSubstringMarkersNotProcessCounts": True,
            "binaryMarkers": agy_markers,
            "sourceFactIds": [
                "agy-data-path",
                "agy-node-image-reference",
                "agy-node-child-spawn",
            ],
        },
        "agentApi": {
            "evidenceLevel": "static-binary-marker-count",
            "helpExecuted": False,
            "valuesCollected": False,
            "publicCommandMarkers": {
                "get-conversation-metadata": agent_api_markers["getConversationMetadata"],
                "new-conversation": agent_api_markers["newConversation"],
                "send-message": agent_api_markers["sendMessage"],
            },
            "addressEnvironmentName": agent_api_markers["addressEnvironmentName"],
            "csrfEnvironmentName": agent_api_markers["csrfEnvironmentName"],
            "csrfUnaryInterceptor": agent_api_markers["csrfUnaryInterceptor"],
            "missingAddressDiagnostic": {
                "exactContiguousMarker": agent_api_markers["missingAddressDiagnostic"],
                "notSetFragment": agent_api_markers["notSetDiagnosticFragment"],
                "compositeStaticPresence": agent_api_markers["addressEnvironmentName"]["totalCount"] > 0
                and agent_api_markers["notSetDiagnosticFragment"]["totalCount"] > 0,
                "runtimeAttributionClaimed": False,
            },
            "commandPathMarker": agent_api_markers["commandPath"],
        },
        "relationshipGraph": _build_graph(facts, oauth_markers, agent_api_markers),
        "limitations": [
            "Static evidence does not prove that a process, file access, port, IPC path, or OAuth flow occurred at runtime.",
            "Unpacked ASAR members are listed from the archive manifest but are deliberately not opened or hashed.",
            "Language-server argument recovery is deliberately fixed and names-only; it is not an exhaustive --help capture.",
            "OAuth recovery uses a fixed safe marker set and is not exhaustive.",
            "The supplied files are hashed individually; vendor provenance and membership in one build cohort are not verified.",
            "Normalized paths of the three explicitly supplied inputs are emitted and may contain caller-controlled directory names.",
        ],
    }
    for input_name, input_path, initial_fingerprint in input_snapshots:
        if _stat_fingerprint(input_path) != initial_fingerprint:
            raise EvidenceError(input_name + " changed during evidence collection.")
    return report


def _write_synthetic_asar(path, members, manifest_entries=None):
    offsets = {}
    payload = bytearray()
    for member_path, content in members.items():
        offsets[member_path] = len(payload)
        payload.extend(content)
    root = {"files": {}}
    for member_path, content in members.items():
        node = root
        parts = member_path.split("/")
        for part in parts[:-1]:
            node = node["files"].setdefault(part, {"files": {}})
        node["files"][parts[-1]] = {
            "size": len(content),
            "offset": str(offsets[member_path]),
        }
    for member_path, entry in (manifest_entries or {}).items():
        node = root
        parts = member_path.split("/")
        for part in parts[:-1]:
            node = node["files"].setdefault(part, {"files": {}})
        node["files"][parts[-1]] = entry
    header = json.dumps(root, separators=(",", ":")).encode("utf-8")
    prefix = struct.pack("<4I", 4, len(header) + 8, len(header) + 4, len(header))
    path.write_bytes(prefix + header + payload)


def run_self_test():
    with tempfile.TemporaryDirectory(prefix="ag-static-evidence-") as temp_directory:
        root = Path(temp_directory)
        antigravity_exe = root / "Antigravity.exe"
        app_asar = root / "app.asar"
        language_server_exe = root / "language_server.exe"
        antigravity_exe.write_bytes(b"MZ synthetic executable fixture\n")
        members = {
            "package.json": b'{\n  "name": "antigravity",\n  "version": "9.9.9",\n  "main": "dist/main.js"\n}\n',
            "dist/constants.js": b"\xfflanguage_server\n",
            "dist/main.js": (
                b"requestSingleInstanceLock();\nwhenReady();\nsetAsDefaultProtocolClient();\n"
                b"startHostBridgeServer();\nstartAndMonitorLanguageServer();\n"
            ),
            "dist/languageServer.js": (
                b"child_process_1.spawn(image, ['--https_server_port', '--csrf_token',\n"
                b"'--app_data_dir', '--host_bridge_url', '--host_bridge_token']);\n"
                b"const neverEmit = 'client_secret=SELF_TEST_SECRET_VALUE';\n"
            ),
            "dist/utils.js": b"const image = 'agy-node'; child_process_1.spawn(image);\n",
        }
        _write_synthetic_asar(
            app_asar,
            members,
            {
                "dist/paths.js": {
                    "size": 64,
                    "unpacked": True,
                }
            },
        )
        language_server_exe.write_bytes(
            b"MZ wincred CredReadW CredWriteW keyring agy-node app_data_dir "
            b"accounts.google.com/o/oauth2/auth oauth2.googleapis.com/device/code "
            b"antigravity:// localhost:%d/auth/callback "
            b"get-conversation-metadata new-conversation send-message "
            b"csrfUnaryInterceptor ANTIGRAVITY_LS_ADDRESS ANTIGRAVITY_CSRF_TOKEN "
            b"ANTIGRAVITY_LS_ADDRESS is not set "
            b"SELF_TEST_SECRET_VALUE"
        )
        report = collect(antigravity_exe, app_asar, language_server_exe)
        serialized = json.dumps(report, ensure_ascii=False, sort_keys=True)
        fact_index = {fact["id"]: fact for fact in report["startupChainFacts"]}
        fact_status = {fact_id: fact["status"] for fact_id, fact in fact_index.items()}
        argument_names = {argument["name"] for argument in report["languageServerHelp"]["publicArguments"]}
        member_index = {member["path"]: member for member in report["asar"]["members"]}
        graph_edges = report["relationshipGraph"]["edges"]
        agy_spawn_edge = next(
            (
                edge
                for edge in graph_edges
                if edge["from"] == "electron-main" and edge["to"] == "agy-node"
            ),
            None,
        )

        linked_member_index = {
            "dist/utils.js": {
                "path": "dist/utils.js",
                "storage": "link-manifest-only",
                "sha256": None,
                "lineCount": None,
            }
        }
        linked_facts = _build_source_facts({}, linked_member_index, set())
        linked_fact_index = {fact["id"]: fact for fact in linked_facts}

        atomic_output = root / "evidence.json"
        atomic_text = '{"selfTest":true}\n'
        _write_output_atomically(
            atomic_output,
            atomic_text,
            (antigravity_exe, app_asar, language_server_exe),
        )
        atomic_publish_succeeded = atomic_output.read_text(encoding="utf-8") == atomic_text
        try:
            _write_output_atomically(
                atomic_output,
                '{"replacement":true}\n',
                (antigravity_exe, app_asar, language_server_exe),
            )
            existing_output_blocked = False
        except EvidenceError:
            existing_output_blocked = atomic_output.read_text(encoding="utf-8") == atomic_text
        checks = {
            "schemaVersion": report["schemaVersion"] == SCHEMA_VERSION,
            "deterministicMemberHash": member_index["package.json"]["sha256"]
            == hashlib.sha256(members["package.json"]).hexdigest(),
            "memberLineCount": member_index["package.json"]["lineCount"] == 5,
            "sourceLocations": fact_status.get("main-starts-language-server") == "present",
            "decodeFailureNotAbsent": fact_status.get("language-server-image-name") == "not-inspected"
            and fact_index["language-server-image-name"]["source"]["inspectionReason"]
            == "utf8-decode-failed",
            "unpackedMemberNotAbsent": all(
                fact_status.get(fact_id) == "not-inspected"
                and fact_index[fact_id]["source"]["inspectionReason"]
                == "external-unpacked-member-not-read"
                for fact_id in ("electron-user-data-path", "process-home-gemini-path", "agy-data-path")
            ),
            "linkMemberNotAbsent": all(
                linked_fact_index[fact_id]["status"] == "not-inspected"
                and linked_fact_index[fact_id]["source"]["inspectionReason"]
                == "link-member-not-followed"
                for fact_id in ("agy-node-image-reference", "agy-node-child-spawn")
            ),
            "staticHelpArguments": "--app_data_dir" in argument_names,
            "oauthMarker": report["oauth"]["markers"]["googleAuthorizationEndpoint"]["totalCount"] == 1,
            "oauthDeviceCodeMarker": report["oauth"]["markers"]["googleDeviceCodeEndpoint"]["totalCount"]
            == 1,
            "oauthProtocolMarker": report["oauth"]["markers"]["antigravityProtocol"]["totalCount"] == 1,
            "oauthMarkersNonExhaustive": report["oauth"]["fixedMarkerSetExhaustive"] is False,
            "winCredMarker": report["credentialLibraries"]["markers"]["credReadW"]["totalCount"] == 1,
            "agyNodeMarker": report["agy"]["binaryMarkers"]["agyNode"]["totalCount"] == 1,
            "agentApiCommands": all(
                marker["totalCount"] == 1
                for marker in report["agentApi"]["publicCommandMarkers"].values()
            ),
            "agentApiEnvironmentNamesOnly": report["agentApi"]["addressEnvironmentName"]["totalCount"] == 2
            and report["agentApi"]["csrfEnvironmentName"]["totalCount"] == 1,
            "secretValueSuppressed": "SELF_TEST_SECRET_VALUE" not in serialized,
            "vendorExecutionDisabled": report["collectionPolicy"]["vendorProcessExecution"] is False,
            "sourceBodyDisabled": report["collectionPolicy"]["vendorSourceCodeEmitted"] is False,
            "inputSnapshotVerified": report["collectionPolicy"]["inputSnapshotConsistencyVerified"] is True,
            "cohortNotClaimed": report["collectionPolicy"]["crossInputCohortVerified"] is False,
            "explicitPathsDisclosed": report["collectionPolicy"]["normalizedExplicitInputPathsEmitted"] is True,
            "edgeEvidenceVocabulary": all(
                edge["evidenceLevel"] in {"direct-static", "correlated", "hypothesized"}
                for edge in graph_edges
            ),
            "agySpawnOnlyCorrelated": agy_spawn_edge is not None
            and agy_spawn_edge["evidenceLevel"] == "correlated",
            "atomicOutputPublished": atomic_publish_succeeded,
            "existingOutputBlocked": existing_output_blocked,
        }
        try:
            _validate_output_target(app_asar, (app_asar,))
            checks["inputOverwriteBlocked"] = False
        except EvidenceError:
            checks["inputOverwriteBlocked"] = True
        passed = all(checks.values())
        return {
            "schemaVersion": SCHEMA_VERSION,
            "evidenceLevel": "synthetic-self-test",
            "selfTest": {
                "passed": passed,
                "checks": checks,
            },
        }


def _parse_arguments(argv):
    parser = argparse.ArgumentParser(
        description="Collect names-only, static evidence without launching Antigravity vendor code."
    )
    parser.add_argument("--antigravity-exe")
    parser.add_argument("--app-asar")
    parser.add_argument("--language-server-exe")
    parser.add_argument("--output", type=Path)
    parser.add_argument("--self-test", action="store_true")
    arguments = parser.parse_args(argv)
    if arguments.self_test:
        if any(
            value is not None
            for value in (
                arguments.antigravity_exe,
                arguments.app_asar,
                arguments.language_server_exe,
                arguments.output,
            )
        ):
            parser.error("--self-test cannot be combined with input or output arguments")
        return arguments
    missing = [
        name
        for name, value in (
            ("--antigravity-exe", arguments.antigravity_exe),
            ("--app-asar", arguments.app_asar),
            ("--language-server-exe", arguments.language_server_exe),
        )
        if value is None
    ]
    if missing:
        parser.error("the following arguments are required unless --self-test is used: " + ", ".join(missing))
    return arguments


def main(argv=None):
    arguments = _parse_arguments(argv)
    try:
        output_path = None
        protected_paths = ()
        if arguments.self_test:
            report = run_self_test()
        else:
            antigravity_exe = _validate_input(arguments.antigravity_exe, "Antigravity.exe")
            app_asar = _validate_input(arguments.app_asar, "app.asar")
            language_server_exe = _validate_input(arguments.language_server_exe, "language_server.exe")
            protected_paths = (
                antigravity_exe,
                app_asar,
                language_server_exe,
                Path(__file__).resolve(strict=True),
            )
            if arguments.output is not None:
                output_path = _validate_output_target(
                    arguments.output,
                    protected_paths,
                )
            report = collect(
                antigravity_exe,
                app_asar,
                language_server_exe,
            )
        output_text = json.dumps(report, ensure_ascii=False, indent=2, sort_keys=True) + "\n"
        if output_path is None:
            sys.stdout.write(output_text)
        else:
            _write_output_atomically(output_path, output_text, protected_paths)
        if arguments.self_test and not report["selfTest"]["passed"]:
            return 1
        return 0
    except EvidenceError as error:
        sys.stderr.write(
            json.dumps(
                {
                    "schemaVersion": SCHEMA_VERSION,
                    "evidenceLevel": "none",
                    "error": {"type": "EvidenceError", "message": str(error)},
                },
                ensure_ascii=False,
                sort_keys=True,
            )
            + "\n"
        )
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
