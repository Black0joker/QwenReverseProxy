"""Run the same cases through the ORIGINAL Python jsonrepair for parity comparison."""
import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2]))
from jsonrepair import jsonrepair

cases = json.loads(Path(sys.argv[1]).read_text(encoding='utf-8'))
for inp in cases:
    output = None
    error = None
    try:
        output = jsonrepair(inp)
        try:
            json.loads(output)
        except Exception as ve:
            error = f"invalid-after-repair: {ve}"
    except Exception as e:
        error = str(e)
    print(json.dumps({"input": inp, "output": output, "error": error}))
