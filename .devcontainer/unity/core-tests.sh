#!/usr/bin/env bash
# Unity を起動せず、Core の試験を速く下調べする。使い方: core-tests.sh --help
exec python3 "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/core-tests.py" "$@"
