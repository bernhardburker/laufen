import os

# Ensure local mock servers are never intercepted by HTTP proxies (e.g. in sandbox or CI environments)
for var in ("no_proxy", "NO_PROXY"):
    current = os.environ.get(var, "")
    if "127.0.0.1" not in current:
        os.environ[var] = f"127.0.0.1,localhost,{current}".rstrip(",")
