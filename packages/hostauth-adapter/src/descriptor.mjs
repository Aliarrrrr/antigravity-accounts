// Only the small FileDescriptorProto needed by the host bridge is generated.
// Field numbers match the reviewed native 2.28.2 GetOAuthToken contract.
function varint(value) {
  const result = [];
  do { result.push((value & 127) | (value > 127 ? 128 : 0)); value = Math.floor(value / 128); } while (value);
  return Buffer.from(result);
}
function text(number, value) { const bytes = Buffer.isBuffer(value) ? value : Buffer.from(value); return Buffer.concat([varint(number * 8 + 2), varint(bytes.length), bytes]); }
function integer(number, value) { return Buffer.concat([varint(number * 8), varint(value)]); }
function field(name, number, type, typeName) {
  return Buffer.concat([text(1, name), integer(3, number), integer(4, 1), integer(5, type), ...(typeName ? [text(6, typeName)] : [])]);
}
function message(name, fields = []) { return Buffer.concat([text(1, name), ...fields.map((item) => text(2, item))]); }
function method(name) { return Buffer.concat([text(1, name), text(2, `.exa.host_bridge_pb.${name}Request`), text(3, `.exa.host_bridge_pb.${name}Response`)]); }
export function descriptorBytes() {
  const namespace = '.exa.host_bridge_pb.';
  const messages = [
    message('GetUpdateStatusRequest'),
    message('GetUpdateStatusResponse', [field('status', 1, 11, namespace + 'UpdateStatus')]),
    message('UpdateStatus', [field('current_version', 1, 9), field('latest_version', 2, 9), field('update_available', 3, 8)]),
    message('ApplyUpdateRequest'),
    message('ApplyUpdateResponse', [field('accepted', 1, 8)]),
    message('GetOAuthTokenRequest'),
    message('GetOAuthTokenResponse', [field('token_info', 1, 11, namespace + 'OAuthTokenInfo')]),
    message('OAuthTokenInfo', [field('access_token', 1, 9), field('token_type', 2, 9), field('refresh_token', 3, 9), field('expiry', 4, 11, '.google.protobuf.Timestamp'), field('is_gcp_tos', 6, 8)]),
  ];
  const service = Buffer.concat([text(1, 'HostBridgeService'), ...['GetUpdateStatus', 'ApplyUpdate', 'GetOAuthToken'].map((name) => text(2, method(name)))]);
  return Buffer.concat([text(1, 'host_bridge.proto'), text(2, 'exa.host_bridge_pb'), text(3, 'google/protobuf/timestamp.proto'), ...messages.map((item) => text(4, item)), text(6, service), text(12, 'proto3')]);
}

export function descriptorSource() {
  const names = ['GetUpdateStatusRequest', 'GetUpdateStatusResponse', 'UpdateStatus', 'ApplyUpdateRequest', 'ApplyUpdateResponse', 'GetOAuthTokenRequest', 'GetOAuthTokenResponse', 'OAuthTokenInfo'];
  return '"use strict";\n// Local 2.28.2 host-auth descriptor extension. Original program retained in restore/app.asar.\n' +
    'Object.defineProperty(exports, "__esModule", { value: true });\n' +
    'const codegen = require("@bufbuild/protobuf/codegenv2");\n' +
    'const wkt = require("@bufbuild/protobuf/wkt");\n' +
    `exports.file_host_bridge = codegen.fileDesc("${descriptorBytes().toString('base64')}", [wkt.file_google_protobuf_timestamp]);\n` +
    names.map((name, index) => `exports.${name}Schema = codegen.messageDesc(exports.file_host_bridge, ${index});\n`).join('') +
    'exports.HostBridgeService = codegen.serviceDesc(exports.file_host_bridge, 0);\n';
}
