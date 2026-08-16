#!/usr/bin/env python3
"""
QuickRemote 发布工具 —— 封装 quickdeploy(文件服务) 与 qdrl(托管平台) 两个 MCP 的常用操作。

目的：避免每次发布都重复编写 MCP 调用脚本（parse/mcp/upload 函数头），显著降低 token 消耗。

配置：从 ~/.workbuddy/mcp.json 读取 MCP 端点与 token（不硬编码密钥）。
目录 ID 为项目固定值，直接内置。

用法：
  python release.py list <dir_id> [--mcp quickdeploy|qdrl]
  python release.py upload <file> <dir_id> [--ext EXT] [--mcp quickdeploy|qdrl]
  python release.py upload-all <dir_id> --ext EXT <file1> [<file2> ...]   # 多文件共享一个令牌
  python release.py delete <file_id> [--mcp quickdeploy|qdrl]
  python release.py upload-root <file1> [<file2> ...]                     # 上传 manifest/changelog 到根目录
  python release.py upgrade-about <tar.gz> <version>                      # 上传 about 包并 upgrade_app
  python release.py apps                                                   # 列出 qdrl 托管应用
"""
import argparse
import json
import os
import re
import sys
import urllib.request
import uuid

# ===== 项目固定的目录 ID（不敏感） =====
DIR_QUICKDEPLOY_ROOT = '5bc66dc8-a607-4384-93a7-1158bf43aed3'
DIR_QUICKDEPLOY_PC = '70fe2927-9101-4aa8-9f7a-f5b4d344ee48'
DIR_QUICKDEPLOY_RELAY = '299b53f5-3472-47fc-963d-3ec0a66d6184'
DIR_QUICKDEPLOY_ANDROID = '5a9ded9b-f935-4a3c-86cd-0532462c3d25'
DIR_QDRL_QUICKREMOTE = '5b681a68-ff94-4a14-bc89-8aab6a200a53'
ABOUT_APP_ID = 'quickremote-about'


def load_config():
    """从 ~/.workbuddy/mcp.json 读取 MCP 端点与 token。"""
    path = os.path.expanduser('~/.workbuddy/mcp.json')
    with open(path, encoding='utf-8') as f:
        data = json.load(f)
    servers = data['mcpServers']

    def extract(name):
        s = servers[name]
        token = s['headers']['Authorization'].replace('Bearer ', '')
        return s['url'], token

    return {'quickdeploy': extract('quickdeploy'), 'qdrl': extract('qdrl')}


class MCP:
    def __init__(self, url, token):
        self.url = url
        self.token = token
        self.sid = None

    def _parse(self, raw):
        raw = raw.strip()
        if raw.startswith('{'):
            return json.loads(raw)
        for line in raw.splitlines():
            if line.startswith('data:'):
                return json.loads(line[5:].strip())
        return {'_raw': raw[:300]}

    def call(self, method, params):
        headers = {
            'Content-Type': 'application/json',
            'Accept': 'application/json, text/event-stream',
            'Authorization': 'Bearer ' + self.token,
        }
        if self.sid:
            headers['Mcp-Session-Id'] = self.sid
        body = json.dumps({'jsonrpc': '2.0', 'id': 1, 'method': method,
                           'params': params or {}}).encode()
        req = urllib.request.Request(self.url, data=body, headers=headers, method='POST')
        resp = urllib.request.urlopen(req, timeout=90)
        if resp.headers.get('Mcp-Session-Id'):
            self.sid = resp.headers.get('Mcp-Session-Id')
        return self._parse(resp.read().decode())

    def tool_text(self, name, args):
        r = self.call('tools/call', {'name': name, 'arguments': args})
        return r['result']['content'][0]['text']


def _upload_one(mcp, filepath, dir_id, ext):
    """上传单个文件，返回 (share_url, file_id, size)。"""
    txt = mcp.tool_text('create_upload_token', {
        'target_dir_id': dir_id, 'permanent_share': True,
        'allow_overwrite': True, 'allowed_extensions': ext})
    m = re.search(r'https://\S+?/api/upload/([A-Za-z0-9_-]+)', txt)
    if not m:
        sys.exit(f'create_upload_token 解析失败，返回：\n{txt[:400]}')
    upload_url = m.group(0)  # 含域名的完整上传 URL
    boundary = uuid.uuid4().hex
    with open(filepath, 'rb') as f:
        content = f.read()
    parts = [
        f'--{boundary}\r\nContent-Disposition: form-data; name="file"; '
        f'filename="{os.path.basename(filepath)}"\r\n'
        f'Content-Type: application/octet-stream\r\n\r\n'.encode(),
        content,
        f'\r\n--{boundary}--\r\n'.encode(),
    ]
    body = b''.join(parts)
    req = urllib.request.Request(upload_url, data=body, method='POST')
    req.add_header('Content-Type', f'multipart/form-data; boundary={boundary}')
    resp = urllib.request.urlopen(req, timeout=300)
    data = json.loads(resp.read().decode())
    return data.get('share_url'), data.get('file_id'), data.get('size')


def cmd_list(args):
    url, token = CONFIG[args.mcp]
    mcp = MCP(url, token)
    mcp.call('initialize', {'protocolVersion': '2024-11-05', 'capabilities': {},
                            'clientInfo': {'name': 'release', 'version': '1.0'}})
    print(mcp.tool_text('list_files', {'dir_id': args.dir_id}))


def cmd_upload(args):
    url, token = CONFIG[args.mcp]
    mcp = MCP(url, token)
    mcp.call('initialize', {'protocolVersion': '2024-11-05', 'capabilities': {},
                            'clientInfo': {'name': 'release', 'version': '1.0'}})
    share, fid, size = _upload_one(mcp, args.file, args.dir_id, args.ext or '')
    print(f'{args.file}\t{share}\t{fid}\t{size}')


def cmd_upload_all(args):
    url, token = CONFIG[args.mcp]
    mcp = MCP(url, token)
    mcp.call('initialize', {'protocolVersion': '2024-11-05', 'capabilities': {},
                            'clientInfo': {'name': 'release', 'version': '1.0'}})
    for f in args.files:
        share, fid, size = _upload_one(mcp, f, args.dir_id, args.ext or '')
        print(f'{f}\t{share}\t{fid}\t{size}')


def cmd_delete(args):
    url, token = CONFIG[args.mcp]
    mcp = MCP(url, token)
    mcp.call('initialize', {'protocolVersion': '2024-11-05', 'capabilities': {},
                            'clientInfo': {'name': 'release', 'version': '1.0'}})
    print(mcp.tool_text('delete_file', {'file_id': args.file_id}))


def cmd_upload_root(args):
    url, token = CONFIG['quickdeploy']
    mcp = MCP(url, token)
    mcp.call('initialize', {'protocolVersion': '2024-11-05', 'capabilities': {},
                            'clientInfo': {'name': 'release', 'version': '1.0'}})
    for f in args.files:
        share, fid, size = _upload_one(mcp, f, DIR_QUICKDEPLOY_ROOT, 'json,md')
        print(f'{f}\t{share}\t{fid}\t{size}')


def cmd_upgrade_about(args):
    url, token = CONFIG['qdrl']
    mcp = MCP(url, token)
    mcp.call('initialize', {'protocolVersion': '2024-11-05', 'capabilities': {},
                            'clientInfo': {'name': 'release', 'version': '1.0'}})
    share, fid, size = _upload_one(mcp, args.tar_gz, DIR_QDRL_QUICKREMOTE, 'tar.gz')
    print(f'上传: {share} (file_id={fid})')
    r = mcp.call('tools/call', {'name': 'upgrade_app', 'arguments': {
        'app_id': ABOUT_APP_ID, 'version': args.version,
        'package_content': 'file://' + fid}})
    txt = r['result']['content'][0]['text']
    print(txt[:300])


def cmd_apps(args):
    url, token = CONFIG['qdrl']
    mcp = MCP(url, token)
    mcp.call('initialize', {'protocolVersion': '2024-11-05', 'capabilities': {},
                            'clientInfo': {'name': 'release', 'version': '1.0'}})
    print(mcp.tool_text('list_apps', {}))


def main():
    parser = argparse.ArgumentParser(description='QuickRemote 发布工具')
    sub = parser.add_subparsers(dest='cmd', required=True)

    p = sub.add_parser('list')
    p.add_argument('dir_id')
    p.add_argument('--mcp', default='quickdeploy', choices=['quickdeploy', 'qdrl'])
    p.set_defaults(func=cmd_list)

    p = sub.add_parser('upload')
    p.add_argument('file')
    p.add_argument('dir_id')
    p.add_argument('--ext', default='')
    p.add_argument('--mcp', default='quickdeploy', choices=['quickdeploy', 'qdrl'])
    p.set_defaults(func=cmd_upload)

    p = sub.add_parser('upload-all')
    p.add_argument('dir_id')
    p.add_argument('--ext', default='')
    p.add_argument('--mcp', default='quickdeploy', choices=['quickdeploy', 'qdrl'])
    p.add_argument('files', nargs='+')
    p.set_defaults(func=cmd_upload_all)

    p = sub.add_parser('delete')
    p.add_argument('file_id')
    p.add_argument('--mcp', default='quickdeploy', choices=['quickdeploy', 'qdrl'])
    p.set_defaults(func=cmd_delete)

    p = sub.add_parser('upload-root')
    p.add_argument('files', nargs='+')
    p.set_defaults(func=cmd_upload_root)

    p = sub.add_parser('upgrade-about')
    p.add_argument('tar_gz')
    p.add_argument('version')
    p.set_defaults(func=cmd_upgrade_about)

    p = sub.add_parser('apps')
    p.set_defaults(func=cmd_apps)

    args = parser.parse_args()
    global CONFIG
    CONFIG = load_config()
    args.func(args)


if __name__ == '__main__':
    main()
