#!/usr/bin/env python3
"""Inspect installed program files only; never open profiles or contact a service.

The JSON output describes declarations and compiled code, not tested runtime
capabilities. Basic inspection uses the standard library; optional Capstone adds
bounded instruction decoding. The client is never executed.
"""

import argparse
import io
import json
import mmap
import re
import struct
import sys
import unittest
from pathlib import Path


LANGUAGE_PROTO = "third_party/jetski/language_server_pb/language_server.proto"
DESCRIPTORS = (
    LANGUAGE_PROTO,
    "third_party/jetski/cortex_pb/cortex.proto",
    "third_party/jetski/project_pb/project.proto",
    "third_party/jetski/jetski_cortex_pb/jetski_cortex.proto",
    "third_party/jetski/codeium_common_pb/codeium_common.proto",
)
AUTH_METHODS = ("GetAuthStatus", "HasAuthToken", "GetUserStatus", "Login", "LoginWithBrowser", "AuthLogout")
STATE_METHODS = (
    "GetAllCascadeTrajectories", "GetCascadeTrajectory", "GetConversationMetadata",
    "WaitForConversationFullyIdle", "ListProjects", "ReadProject",
    "GetBrowserOpenConversation", "SetBrowserOpenConversation", "SmartFocusConversation",
)
TOKEN_METHODS = ("SwitchAccount", "SetAuthToken", "ReloadAuth", "ListAccounts")
SCALAR_TYPES = {
    1: "double", 2: "float", 3: "int64", 4: "uint64", 5: "int32",
    6: "fixed64", 7: "fixed32", 8: "bool", 9: "string", 12: "bytes",
    13: "uint32", 15: "sfixed32", 16: "sfixed64", 17: "sint32", 18: "sint64",
}


def varint(data, offset):
    value = 0
    for shift in range(0, 70, 7):
        if offset >= len(data):
            raise ValueError("Truncated protobuf varint")
        byte = data[offset]
        offset += 1
        value |= (byte & 127) << shift
        if byte < 128:
            return value, offset
    raise ValueError("Invalid protobuf varint")


def encode_varint(value):
    result = bytearray()
    while value > 127:
        result.append((value & 127) | 128)
        value >>= 7
    result.append(value)
    return bytes(result)


def wire_fields(data):
    offset = 0
    while offset < len(data):
        tag, offset = varint(data, offset)
        number, wire = tag >> 3, tag & 7
        if not number:
            raise ValueError("Invalid protobuf field zero")
        if wire == 0:
            value, offset = varint(data, offset)
        elif wire in (1, 5):
            end = offset + (8 if wire == 1 else 4)
            value, offset = data[offset:end], end
        elif wire == 2:
            size, offset = varint(data, offset)
            end = offset + size
            value, offset = data[offset:end], end
        else:
            raise ValueError("Unsupported protobuf wire type")
        if offset > len(data):
            raise ValueError("Truncated protobuf field")
        yield number, value


def values(data):
    result = {}
    for number, value in wire_fields(data):
        result.setdefault(number, []).append(value)
    return result


def text_field(fields, number, default=""):
    item = fields.get(number, [None])[0]
    return item.decode("utf-8") if isinstance(item, bytes) else default


def int_field(fields, number, default=0):
    item = fields.get(number, [default])[0]
    return item if isinstance(item, int) else default


def extract_descriptor(binary, filename):
    name = filename.encode("utf-8")
    start = binary.find(b"\x0a" + encode_varint(len(name)) + name)
    if start < 0:
        raise ValueError("File descriptor not found: " + filename)
    offset = start
    first = True
    while offset < len(binary):
        tag, position = varint(binary, offset)
        field, wire = tag >> 3, tag & 7
        if not 1 <= field <= 14 or (field == 1 and not first):
            raise ValueError("Descriptor boundary missing: " + filename)
        first = False
        if wire == 2:
            size, position = varint(binary, position)
            position += size
        elif wire == 0:
            _, position = varint(binary, position)
        elif wire in (1, 5):
            position += 8 if wire == 1 else 4
        else:
            raise ValueError("Invalid file descriptor wire type")
        if position > len(binary):
            raise ValueError("Truncated file descriptor")
        offset = position
        # protoc serializes syntax after definitions/options; this is the
        # observed proto3 boundary, rather than a guessed fixed byte count.
        if field == 12:
            fields = values(binary[start:offset])
            if text_field(fields, 1) != filename or text_field(fields, 12) != "proto3":
                raise ValueError("Unsupported descriptor syntax")
            return fields, start, offset
        if offset - start > 8 * 1024 * 1024:
            raise ValueError("Descriptor exceeds inspection limit")
    raise ValueError("Descriptor is incomplete")


def parse_enum(data, prefix, enums):
    fields = values(data)
    full_name = prefix + "." + text_field(fields, 1)
    enum = {}
    for item in (values(raw) for raw in fields.get(2, [])):
        number = int_field(item, 2)
        enum[text_field(item, 1)] = number - (1 << 64) if number >= (1 << 63) else number
    enums[full_name] = enum


def parse_message(data, prefix, messages, enums):
    fields = values(data)
    full_name = prefix + "." + text_field(fields, 1)
    result = []
    for raw in fields.get(2, []):
        item = values(raw)
        kind = int_field(item, 5)
        result.append({
            "name": text_field(item, 1), "number": int_field(item, 3),
            "jsonName": text_field(item, 10),
            "type": text_field(item, 6) or SCALAR_TYPES.get(kind, "unknown"),
            "repeated": int_field(item, 4) == 3,
        })
    messages[full_name] = result
    for raw in fields.get(3, []):
        parse_message(raw, full_name, messages, enums)
    for raw in fields.get(4, []):
        parse_enum(raw, full_name, enums)


def parse_file(fields, messages, enums):
    prefix = "." + text_field(fields, 2)
    for raw in fields.get(4, []):
        parse_message(raw, prefix, messages, enums)
    for raw in fields.get(5, []):
        parse_enum(raw, prefix, enums)
    methods = {}
    for raw_service in fields.get(6, []):
        service = values(raw_service)
        full_name = prefix.lstrip(".") + "." + text_field(service, 1)
        for raw_method in service.get(2, []):
            method = values(raw_method)
            name = text_field(method, 1)
            methods[name] = {
                "rpcPath": "/" + full_name + "/" + name,
                "inputType": text_field(method, 2), "outputType": text_field(method, 3),
                "clientStreaming": bool(int_field(method, 5)),
                "serverStreaming": bool(int_field(method, 6)),
            }
    return methods


class Asar:
    """Read named code entries without extracting files or executing Node."""

    def __init__(self, filename):
        self.filename = filename
        with filename.open("rb") as archive:
            header = archive.read(16)
            if len(header) != 16:
                raise ValueError("Invalid ASAR header")
            _, header_size, _, json_size = struct.unpack("<IIII", header)
            if not 0 < json_size <= 32 * 1024 * 1024:
                raise ValueError("Invalid ASAR header size")
            self.files = json.loads(archive.read(json_size))
            self.data_start = 8 + header_size

    def read(self, name):
        entry = self.files
        for component in name.split("/"):
            entry = entry["files"][component]
        size = entry["size"]
        if size > 2 * 1024 * 1024 or entry.get("link"):
            raise ValueError("Unsupported ASAR code entry")
        if entry.get("unpacked"):
            path = Path(str(self.filename) + ".unpacked").joinpath(*name.split("/"))
            return path.read_bytes().decode("utf-8")
        with self.filename.open("rb") as archive:
            archive.seek(self.data_start + int(entry["offset"]))
            content = archive.read(size)
        if len(content) != size:
            raise ValueError("Truncated ASAR code entry")
        return content.decode("utf-8")


def selected_schemas(methods, messages, enums):
    selected_messages, selected_enums = {}, {}
    queue = [(method[key], 0) for method in methods.values() for key in ("inputType", "outputType")]
    queue.extend((name, 1) for name in (
        ".exa.project_pb.ProjectConversation",
        ".exa.jetski_cortex_pb.CascadeTrajectorySummary",
    ))
    while queue:
        name, depth = queue.pop(0)
        if name in enums:
            selected_enums[name] = enums[name]
        if name not in messages or name in selected_messages:
            continue
        selected_messages[name] = messages[name]
        if depth < 2:
            queue.extend((field["type"], depth + 1) for field in messages[name])
    # The state enum is directly relevant even when another schema is absent.
    for name, enum in enums.items():
        if name.endswith(".CascadeRunStatus"):
            selected_enums[name] = enum
    return selected_messages, selected_enums


def go_program(binary):
    """Locate PE sections and Go function boundaries without executing code."""
    if binary[:2] != b"MZ":
        raise ValueError("Expected a Windows PE binary")
    pe = struct.unpack_from("<I", binary, 60)[0]
    count = struct.unpack_from("<H", binary, pe + 6)[0]
    optional_size = struct.unpack_from("<H", binary, pe + 20)[0]
    image_base = struct.unpack_from("<Q", binary, pe + 48)[0]
    sections = []
    for index in range(count):
        offset = pe + 24 + optional_size + index * 40
        name = binary[offset:offset + 8].split(b"\x00")[0]
        virtual_size, address, raw_size, raw_offset = struct.unpack_from("<IIII", binary, offset + 8)
        sections.append((name, address, max(virtual_size, raw_size), raw_offset))
    text_base = image_base + next(address for name, address, _, _ in sections if name == b".text")
    header = binary.find(b"\xf1\xff\xff\xff\x00\x00\x01\x08")
    if header < 0:
        raise ValueError("Unsupported or missing Go PC table")
    function_count, _, text, names_offset, _, _, _, table_offset = struct.unpack_from("<8Q", binary, header + 8)
    if not 0 < function_count < 2000000:
        raise ValueError("Invalid Go function count")
    table = header + table_offset
    names = header + names_offset
    rows = []
    for index in range(function_count):
        entry, info = struct.unpack_from("<II", binary, table + index * 8)
        end = struct.unpack_from("<I", binary, table + (index + 1) * 8)[0]
        name_offset = struct.unpack_from("<i", binary, table + info + 4)[0]
        start = names + name_offset
        stop = binary.find(b"\x00", start, start + 2048)
        if stop < 0:
            raise ValueError("Invalid Go function name")
        name = binary[start:stop].decode("utf-8")
        base = text or text_base
        rows.append((base + entry, base + end, name))
    return image_base, sections, rows


def program_offset(program, address):
    image_base, sections, _ = program
    rva = address - image_base
    for _, base, size, raw in sections:
        if base <= rva < base + size:
            return raw + rva - base
    raise ValueError("Address is outside the program sections")


def go_call_candidates(binary, program=None):
    """Byte-level call candidates, deliberately separate from decoded instructions."""
    program = program or go_program(binary)
    _, _, rows = program
    targets = {start: name for start, _, name in rows}
    prefix = "google3/third_party/jetski/language_server/code_assist_client/codeassistclient."
    selected = {
        prefix + suffix for suffix in (
            "NewFileTokenStorage", "(*fileTokenStorage).updateStoredToken",
            "(*fileTokenStorage).LoadStoredToken", "(*KeyringTokenStorage).LoadStoredToken",
        )
    }
    result = {}
    for start, end, name in rows:
        if name not in selected:
            continue
        file_offset = program_offset(program, start)
        body = binary[file_offset:file_offset + end - start]
        called = []
        for match in re.finditer(b"\xe8", body):
            offset = match.start()
            if offset + 5 > len(body):
                continue
            target = start + offset + 5 + struct.unpack_from("<i", body, offset + 1)[0]
            if target in targets:
                called.append(targets[target])
        result[name] = list(dict.fromkeys(called))
    return result


def declaration_from_arguments(arguments, function, address):
    """Only a decoded declareFlag call with complete literal arguments counts."""
    name = arguments.get("name")
    usage = arguments.get("usage")
    if name not in ("gemini_dir", "config_dir", "app_data_dir") or not usage:
        return None
    return {"name": name, "registration": "decoded_declareFlag_call",
            "function": function, "callAddress": hex(address), "usage": usage,
            "runtimeValidated": False}


def go_instruction_details(binary, program):
    """Optional instruction decoding; absence of Capstone leaves facts unconfirmed.

    This recognizes bounded literal argument sequences in named Go functions.
    It does not emulate the binary or infer which authentication branch runs.
    """
    result = {"decoder": "unavailable", "flags": {}, "authStorage": {},
              "runtimeSelection": "unconfirmed"}
    try:
        import capstone
    except ImportError:
        result["reason"] = "Optional Capstone is not installed; literal strings alone are not declarations."
        return result
    result["decoder"] = "capstone_x86_64"
    result["decoderVersion"] = capstone.__version__
    decoder = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
    decoder.detail = True
    _, _, rows = program
    targets = {start: name for start, _, name in rows}
    x86 = capstone.x86

    def instructions(suffix):
        matches = [row for row in rows if row[2] == suffix or row[2].endswith("." + suffix)]
        if len(matches) != 1:
            return "", []
        start, end, name = matches[0]
        if end - start > 1024 * 1024:
            raise ValueError("Selected function exceeds instruction inspection limit")
        offset = program_offset(program, start)
        return name, list(decoder.disasm(binary[offset:offset + end - start], start))

    def register(name):
        return {"eax": "rax", "ebx": "rbx", "ecx": "rcx", "edx": "rdx",
                "edi": "rdi", "esi": "rsi", "r8d": "r8", "r9d": "r9",
                "r10d": "r10"}.get(name, name)

    def literal_before(items, index, pointer, length):
        address = size = None
        for instruction in reversed(items[max(0, index - 20):index]):
            if instruction.mnemonic == "call":
                break
            operands = instruction.operands
            if len(operands) != 2 or operands[0].type != x86.X86_OP_REG:
                continue
            dest = register(instruction.reg_name(operands[0].reg))
            source = operands[1]
            if address is None and dest == pointer and instruction.mnemonic == "lea" and source.type == x86.X86_OP_MEM and source.mem.base == x86.X86_REG_RIP:
                address = instruction.address + instruction.size + source.mem.disp
            if size is None and dest == length and instruction.mnemonic == "mov" and source.type == x86.X86_OP_IMM:
                size = source.imm
        if address is None or size is None or not 0 < size <= 1024:
            return None
        offset = program_offset(program, address)
        value = bytes(binary[offset:offset + size]).decode("utf-8")
        return value if all(char.isprintable() for char in value) else None

    def call_name(instruction):
        if instruction.mnemonic == "call" and instruction.operands[0].type == x86.X86_OP_IMM:
            return targets.get(instruction.operands[0].imm, "")
        return ""

    def fragment(items):
        return [{"address": hex(item.address), "instruction": item.mnemonic + " " + item.op_str}
                for item in items]

    def literal_stores(items):
        result = []
        for index in range(1, len(items) - 1):
            previous, item, following = items[index - 1:index + 2]
            if previous.mnemonic != "mov" or item.mnemonic != "lea" or following.mnemonic != "mov":
                continue
            before, current, after = previous.operands, item.operands, following.operands
            if not all(len(operands) == 2 for operands in (before, current, after)):
                continue
            if (before[0].type != x86.X86_OP_MEM or before[1].type != x86.X86_OP_IMM
                    or current[0].type != x86.X86_OP_REG or current[1].type != x86.X86_OP_MEM
                    or current[1].mem.base != x86.X86_REG_RIP
                    or after[0].type != x86.X86_OP_MEM or after[1].type != x86.X86_OP_REG
                    or after[1].reg != current[0].reg
                    or before[0].mem.base != after[0].mem.base
                    or before[0].mem.disp != after[0].mem.disp + 8):
                continue
            size = before[1].imm
            if not 0 < size <= 256:
                continue
            offset = program_offset(program, item.address + item.size + current[1].mem.disp)
            value = bytes(binary[offset:offset + size]).decode("utf-8")
            if value.isprintable():
                result.append({"value": value, "address": hex(item.address),
                               "store": following.op_str})
        return result

    name, items = instructions("main.init.0")
    for index, instruction in enumerate(items):
        if call_name(instruction).endswith("flag.(*FlagSet).declareFlag"):
            declaration = declaration_from_arguments({
                "name": literal_before(items, index, "rdi", "rsi"),
                "usage": literal_before(items, index, "r8", "r9"),
            }, name, instruction.address)
            if declaration:
                result["flags"][declaration["name"]] = declaration

    name, items = instructions("NewFileTokenStorage")
    calls = [call_name(item) for item in items if call_name(item)]
    result["authStorage"]["fileConstructor"] = {
        "function": name, "directCalls": calls,
        "pathConstruction": "unconfirmed", "runtimeValidated": False,
    }
    literals = [item for item in literal_stores(items)
                if item["value"] in (".gemini", "jetski-standalone-oauth-token")]
    result["authStorage"]["fileConstructor"]["decodedPathLiterals"] = literals
    if ([item["value"] for item in literals] == [".gemini", "jetski-standalone-oauth-token"]
            and "os.UserHomeDir" in calls and "path/filepath.join" in calls):
        result["authStorage"]["fileConstructor"]["pathConstruction"] = "UserHomeDir/.gemini/jetski-standalone-oauth-token (decoded constructor)"
    # Record the actual constructor instructions instead of guessing a path
    # from the filename's presence elsewhere in the binary.
    join_index = next((i for i, item in enumerate(items) if call_name(item) == "path/filepath.join"), None)
    if join_index is not None:
        result["authStorage"]["fileConstructor"]["joinInstructions"] = fragment(items[max(0, join_index - 15):join_index + 1])

    name, items = instructions("(*KeyringTokenStorage).LoadStoredToken")
    service = None
    for index, item in enumerate(items):
        if item.mnemonic == "call" and item.operands[0].type == x86.X86_OP_REG:
            service = literal_before(items, index, "rbx", "rcx")
            if service:
                break
    result["authStorage"]["keyringLoad"] = {
        "function": name, "serviceArgument": service,
        "accountArgument": "receiver_string_field; caller selection unconfirmed",
        "namespaceIsolationByHome": "not_established",
    }
    name, items = instructions("windowsKeychain.Get")
    calls = [call_name(item) for item in items if call_name(item)]
    separator = next((literal_before(items, i, "rdi", "rsi") for i, item in enumerate(items)
                      if call_name(item) == "runtime.concatstring3"), None)
    result["authStorage"]["windowsKeyring"] = {
        "function": name, "concatenationSeparator": separator,
        "directCalls": calls, "accountConstructorSelection": "unconfirmed",
    }
    name, items = instructions("NewAuthClient")
    result["authStorage"]["authClientConstructor"] = {
        "function": name,
        "decodedAccountLiteralStores": [item for item in literal_stores(items) if item["value"] == "antigravity"],
        "compositeStorageCallPresent": any(call_name(item).endswith(".NewCompositeTokenStorage") for item in items),
        "selectedBackend": "unconfirmed",
    }
    accounts = result["authStorage"]["authClientConstructor"]["decodedAccountLiteralStores"]
    if service == "gemini" and separator == ":" and len(accounts) == 1:
        result["authStorage"]["windowsKeyring"]["constructedCredentialTarget"] = "gemini:antigravity"
        result["authStorage"]["windowsKeyring"]["targetScope"] = "decoded_constructor_path; runtime_backend_unconfirmed"
    name, items = instructions("NewCompositeTokenStorage")
    result["authStorage"]["compositeConstructor"] = {
        "function": name,
        "directCalls": list(dict.fromkeys(call_name(item) for item in items if call_name(item))),
        "branchMessages": [message for i, item in enumerate(items)
                           if call_name(item).endswith("log.ctxlogf")
                           and (message := literal_before(items, i, "r9", "r10"))],
        "selectedBackend": "unconfirmed",
    }
    return result


def source_fragment(source, term, after=None):
    active = after is None
    for number, line in enumerate(source.splitlines(), 1):
        if after is not None and after in line:
            active = True
        if active and term in line:
            return {"line": number, "code": line.strip()}
    return None


def host_startup_report(sources):
    """Report observed startup code; no source match can enable the adapter."""
    main, language = sources["main"], sources["languageServer"]
    ipc, menu = sources["ipc"], sources["menu"]
    option_match = re.search(r"const\s*\{([^}]+)\}\s*=\s*options", language)
    options = [item.strip() for item in option_match[1].split(",")] if option_match else []
    args_match = re.search(r"const args = \[([\s\S]*?)\];", language)
    flags = re.findall(r"'(--[a-z_]+)'", args_match[1]) if args_match else []
    channels = re.findall(r"ipcMain\.handle\('([^']+)'", ipc)
    return {
        "bridgeMethods": re.findall(r"async (\w+)\(\)", sources["bridge"]),
        "ipcChannels": channels,
        "startupOptions": options, "fixedSpawnFlags": flags,
        "directoryOverrideFlagsPassed": {name: "--" + name in flags for name in ("gemini_dir", "config_dir", "app_data_dir")},
        "childEnvironmentCode": source_fragment(language, "const env ="),
        "windowsShellEnvironmentCode": source_fragment(sources["shellEnv"], "return process.env;", after="export function shellEnvSync"),
        "profileReferenceOptionObserved": any(item in options for item in ("profile", "profileId", "home", "env", "environment")),
        "profileSwitchIpcChannels": [channel for channel in channels if re.search(r"profile|account|restart.*(?:ls|server)", channel, re.I)],
        "restartReusesOptions": "startLanguageServer(port, csrf, options)" in language,
        "reloadsWindowsAtRootAfterBackendPortChange": "win.loadURL(newUrl)" in main and "${newPort}/" in main,
        "portChangeRecoveryCode": source_fragment(main, "win.loadURL(newUrl)"),
        "nativeWslSwitchRelaunchesApp": "app.relaunch({ args })" in menu and "app.quit()" in menu,
        "opensRequestedBrowserUrl": source_fragment(language, "shell.openExternal(openUrl)"),
        "workRootCode": source_fragment(sources["paths"], "'.gemini', getAppDataDirName()"),
        "configurationRootCode": source_fragment(sources["paths"], "'.gemini', 'config'"),
        "conversationRoute": "unconfirmed", "projectRoute": "unconfirmed",
        "draftAndViewRestore": "unconfirmed",
        "rootOrigin": "https://127.0.0.1:<language-server-port>/",
    }


def inspect(install_dir):
    resources = install_dir / "resources"
    binary_path = resources / "bin" / "language_server.exe"
    archive = Asar(resources / "app.asar")
    package = json.loads(archive.read("package.json"))
    report = {
        "schemaVersion": 1, "inspection": "installed_program_files_only",
        "clientVersion": package.get("version"), "runtimeValidated": False,
        "credentialFilesRead": False, "networkRequests": 0,
        "binaryBytes": binary_path.stat().st_size,
        "descriptors": [], "methods": {}, "schemas": {}, "enums": {},
    }
    with binary_path.open("rb") as binary_file, mmap.mmap(binary_file.fileno(), 0, access=mmap.ACCESS_READ) as binary:
        messages, enums, all_methods = {}, {}, {}
        for filename in DESCRIPTORS:
            try:
                fields, start, end = extract_descriptor(binary, filename)
                methods = parse_file(fields, messages, enums)
                all_methods.update(methods)
                report["descriptors"].append({"name": filename, "offset": start, "bytes": end - start})
            except ValueError as error:
                report["descriptors"].append({"name": filename, "error": str(error)})
        if any("error" in descriptor for descriptor in report["descriptors"]):
            report["inspection"] = "incomplete_program_descriptor_extraction"
        for name in AUTH_METHODS + STATE_METHODS:
            if name not in all_methods:
                report["methods"][name] = {"declared": False}
                continue
            method = dict(all_methods[name], declared=True)
            symbol = "google3/third_party/jetski/language_server/language_server.(*Server)." + name
            method["serverSymbolPresent"] = binary.find(symbol.encode() + b"\x00") >= 0
            report["methods"][name] = method
        report["tokenReplacementMethods"] = {name: name in all_methods for name in TOKEN_METHODS}
        setter = re.compile(rb"google3/[A-Za-z0-9_/().*]+\.SetAuthToken\x00")
        owners = sorted({m.group()[:-1].decode("ascii") for m in setter.finditer(binary)})
        report["setAuthTokenSymbols"] = {
            "count": len(owners), "examples": owners[:4],
            "interpretation": "Field setters and unrelated clients are not token-replacement RPCs.",
        }
        known_methods = {name: method for name, method in report["methods"].items() if method.get("declared")}
        report["schemas"], report["enums"] = selected_schemas(known_methods, messages, enums)
        symbols = (
            "google3/third_party/jetski/cortex/trajectory/dbtrajectory/dbtrajectory.newOrLoad",
            "google3/third_party/jetski/language_server/projects/projects.(*Store).GetProject",
            "google3/third_party/jetski/cortex/protostore/protostore.NewFileStore",
        )
        report["storage"] = {
            "compiledSymbols": {symbol: binary.find(symbol.encode()) >= 0 for symbol in symbols},
            "conversationSummariesDbLiteral": binary.find(b"conversation_summaries.db") >= 0,
            "sqliteRuntimePresent": binary.find(b"SQLite format 3\x00") >= 0,
            "conversationProjectDbFormat": "unconfirmed",
            "accountBindingAndLocking": "unconfirmed",
            "warning": "SQLite/GORM/protobuf symbols do not establish a particular file's format or safe sharing semantics.",
        }
        try:
            program = go_program(binary)
            report["authStorage"] = {
                "tokenFilenameLiteral": "jetski-standalone-oauth-token" if binary.find(b"jetski-standalone-oauth-token") >= 0 else None,
                "relativeCallCandidates": go_call_candidates(binary, program),
                "storedTokenSchema": "unconfirmed",
                "exactPathAndKeyringSelection": "see_instructionAnalysis; runtime_selection_unconfirmed",
                "replacementSafety": "unconfirmed",
            }
            report["instructionAnalysis"] = go_instruction_details(binary, program)
        except (ValueError, struct.error, StopIteration) as error:
            report["authStorage"] = {"inspectionError": str(error), "replacementSafety": "unconfirmed"}
    report["host"] = host_startup_report({
        "bridge": archive.read("dist/hostBridgeServer.js"),
        "ipc": archive.read("dist/ipcHandlers.js"),
        "main": archive.read("dist/main.js"),
        "languageServer": archive.read("dist/languageServer.js"),
        "menu": archive.read("dist/menu.js"), "paths": archive.read("dist/paths.js"),
        "shellEnv": archive.read("node_modules/shell-env/index.js"),
    })
    report["adapterDecision"] = {
        "status": "unsupported", "adapterSupported": False,
        "automaticAccountReplacement": "unsupported",
        "credentialManagement": "not_implemented",
        "officialProfileReferenceSwitch": "not_exposed_by_observed_host",
        "runningTaskDetection": "declared_protocol_only",
        "backendRestartRequired": "unconfirmed",
        "conversationRestore": "unconfirmed",
        "requiredBeforeRuntimeAdapter": [
            "Confirm official authentication selection for an existing Profile without Hub reading or saving credentials.",
            "Confirm the runtime meaning of directory overrides and their separation from the authentication HOME.",
            "Confirm Windows keyring namespace and file/keyring selection; changing HOME is not proof of account isolation.",
            "Confirm all active conversations and background tasks are represented before an idle decision.",
            "Confirm same-conversation, draft and view recovery after backend restart.",
            "Confirm failure restores the prior backend without opening a browser or requiring confirmation.",
        ],
    }
    return report


def self_test():
    class StaticReportTests(unittest.TestCase):
        def test_literals_without_registration_do_not_count(self):
            self.assertIsNone(declaration_from_arguments({"name": "gemini_dir"}, "main.init.0", 1))
            self.assertIsNone(declaration_from_arguments({"name": "other", "usage": "help"}, "main.init.0", 1))
            self.assertFalse(declaration_from_arguments({"name": "gemini_dir", "usage": "help"}, "main.init.0", 1)["runtimeValidated"])

        def test_decoded_call_is_required_for_a_flag(self):
            try:
                import capstone  # noqa: F401 - optional decoder availability
            except ImportError:
                self.skipTest("Optional Capstone is unavailable")
            data = bytearray(256)
            data[160:170] = b"gemini_dir"
            data[180:192] = b"A test path."
            code = (b"\x48\x8d\x3d" + struct.pack("<i", 160 - 7)
                    + b"\xbe\x0a\x00\x00\x00"
                    + b"\x4c\x8d\x05" + struct.pack("<i", 180 - 19)
                    + b"\x41\xb9\x0c\x00\x00\x00"
                    + b"\xe8" + struct.pack("<i", 128 - 30) + b"\xc3")
            data[:len(code)] = code
            program = (0, [(b".text", 0, len(data), 0)], [
                (0, len(code), "main.init.0"),
                (128, 129, "google3/base/go/flag.(*FlagSet).declareFlag"),
            ])
            self.assertEqual("A test path.", go_instruction_details(data, program)["flags"]["gemini_dir"]["usage"])
            data[25:30] = b"\x90" * 5
            self.assertEqual({}, go_instruction_details(data, program)["flags"])

        def test_host_distinguishes_window_reload_from_profile_switch(self):
            report = host_startup_report({
                "main": "const newUrl = `${newPort}/`; win.loadURL(newUrl);",
                "languageServer": "const { headless, wsl } = options; const args = ['--standalone', '--app_data_dir'];\nconst env = { ...process.env };\nstartLanguageServer(port, csrf, options)",
                "ipc": "ipcMain.handle('wsl:connect', handler);",
                "menu": "app.relaunch({ args }); app.quit();", "bridge": "async getUpdateStatus() {}",
                "paths": "", "shellEnv": "return process.env;",
            })
            self.assertTrue(report["reloadsWindowsAtRootAfterBackendPortChange"])
            self.assertTrue(report["restartReusesOptions"])
            self.assertFalse(report["profileReferenceOptionObserved"])
            self.assertEqual([], report["profileSwitchIpcChannels"])
            self.assertEqual({"gemini_dir": False, "config_dir": False, "app_data_dir": True}, report["directoryOverrideFlagsPassed"])

        def test_missing_host_code_does_not_claim_observed_behavior(self):
            report = host_startup_report(dict.fromkeys(("main", "languageServer", "ipc", "menu", "bridge", "paths", "shellEnv"), ""))
            self.assertFalse(report["reloadsWindowsAtRootAfterBackendPortChange"])
            self.assertFalse(report["nativeWslSwitchRelaunchesApp"])
            self.assertIsNone(report["childEnvironmentCode"])
    output = io.StringIO()
    result = unittest.TextTestRunner(stream=output, verbosity=2).run(unittest.defaultTestLoader.loadTestsFromTestCase(StaticReportTests))
    print(json.dumps({"selfTest": "passed" if result.wasSuccessful() else "failed", "testsRun": result.testsRun,
                      "skipped": len(result.skipped),
                      "runtimeValidated": False, "credentialFilesRead": False, "networkRequests": 0,
                      "failures": [failure for _, failure in result.failures + result.errors]}, ensure_ascii=False, indent=2))
    return 0 if result.wasSuccessful() else 1


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--install-dir", type=Path, help="Official Antigravity installation directory; not a Profile directory")
    parser.add_argument("--method", choices=AUTH_METHODS + STATE_METHODS, help="Emit only one declared method and its request/response schemas; no request is sent")
    parser.add_argument("--self-test", action="store_true", help="Test report logic with in-memory inputs only")
    args = parser.parse_args()
    if args.self_test:
        if args.install_dir or args.method:
            parser.error("--self-test cannot be combined with installation inspection")
        return self_test()
    if not args.install_dir:
        parser.error("--install-dir is required unless --self-test is selected")
    try:
        report = inspect(args.install_dir.resolve(strict=True))
    except (OSError, ValueError, KeyError, json.JSONDecodeError) as error:
        print(json.dumps({"schemaVersion": 1, "inspection": "failed", "error": str(error), "runtimeValidated": False}, ensure_ascii=False))
        return 1
    complete = report["inspection"] == "installed_program_files_only"
    if args.method:
        method = report["methods"][args.method]
        report = {
            "schemaVersion": 1, "clientVersion": report["clientVersion"],
            "runtimeValidated": False, "networkRequests": 0,
            "method": method,
            "inputFields": report["schemas"].get(method.get("inputType")),
            "outputFields": report["schemas"].get(method.get("outputType")),
        }
    print(json.dumps(report, ensure_ascii=False, indent=2))
    return 0 if complete else 1


if __name__ == "__main__":
    sys.exit(main())
