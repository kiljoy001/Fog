# wasi-testsuite's adapter for Fog: runs each test with fog-wasi, which takes Wasmtime's arguments
# for what the suite asks. FOG_WASI is the command that starts it, by default "fog-wasi".
import os
import shlex
from typing import Dict, List, Optional, Tuple

FOG_WASI = shlex.split(os.getenv("FOG_WASI", "fog-wasi"))


def get_name() -> str:
    return "fog"


def get_version() -> str:
    return os.getenv("FOG_WASI_VERSION", "wasi")


def get_wasi_versions() -> List[str]:
    return ["wasm32-wasip1"]


def get_wasi_worlds() -> List[str]:
    return ["wasi:cli/command"]


def compute_argv(test_path: str,
                 args_env_root: Tuple[List[str], Dict[str, str], Optional[str]],
                 proposals: List[str],
                 wasi_world: str,
                 wasi_version: str) -> List[str]:
    args, env, root = args_env_root
    argv = list(FOG_WASI)
    for name, value in env.items():
        argv += ["--env", f"{name}={value}"]
    if root:
        argv += ["--dir", f"{root}::/"]
    return argv + [test_path] + args
