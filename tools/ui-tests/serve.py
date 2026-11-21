#!/usr/bin/env python3
"""Static file server for the UI tests: usage: serve.py <directory> <port>

Like `python -m http.server`, but pins the MIME types browsers care about. On Windows, mimetypes.guess_type()
consults HKEY_CURRENT_USER\\SOFTWARE\\Classes, and some machines have that key reassign '.js' to text/plain (seen
from other installed software) -- browsers refuse to run a <script type="module"> served with that type, which
breaks every page silently and fails nearly the whole suite with no link back to this cause.

The types are set on the handler's extensions_map, which guess_type() checks before the mimetypes module on every
supported Python. mimetypes.add_type() would not do: on Python 3.8 the handler copies mimetypes.types_map when
http.server is imported, so later additions never reach it.
"""
import functools
import http.server
import sys


class Handler(http.server.SimpleHTTPRequestHandler):
    extensions_map = {
        **http.server.SimpleHTTPRequestHandler.extensions_map,
        '.js': 'text/javascript',
        '.mjs': 'text/javascript',
        '.css': 'text/css',
        '.json': 'application/json',
    }


directory, port = sys.argv[1], int(sys.argv[2])
http.server.test(HandlerClass=functools.partial(Handler, directory=directory), port=port, bind='127.0.0.1')
