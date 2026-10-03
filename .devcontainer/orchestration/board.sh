#!/bin/bash
# usage: board.sh <op> '<json>' — 掲示板の操作を指揮役のトークン（~/.local/share/agent-board/claude-orchestrator.token）で呼ぶ
T=$(cat ~/.local/share/agent-board/claude-orchestrator.token)
curl -s -X POST -H "Authorization: Bearer $T" -H "Content-Type: application/json" -H "Origin: http://127.0.0.1:8787" --data "$2" "http://127.0.0.1:8787/api/ops/$1"
