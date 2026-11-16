# Copyright (c) 2026 Shin-plaster
# SPDX-License-Identifier: MIT

"""One import point for the MCP SDK: version 2 renamed ``FastMCP`` to ``MCPServer``; version 1 has ``FastMCP``."""

try:
    from mcp.server.mcpserver import Image, MCPServer as Server  # mcp 2.x
    from mcp.server.mcpserver.exceptions import ToolError
except ImportError:
    try:
        from mcp.server.fastmcp import FastMCP as Server, Image  # mcp 1.x
        from mcp.server.fastmcp.exceptions import ToolError
    except ImportError as exc:
        raise ImportError("the MCP server needs the MCP SDK: pip install icescreentouch[mcp]") from exc

from mcp.types import ToolAnnotations  # the same import path in mcp 1.x and 2.x
from pydantic import Field  # a dependency of the MCP SDK: imported here so a missing extra gives the hint above first

__all__ = ["Field", "Image", "Server", "ToolAnnotations", "ToolError"]
