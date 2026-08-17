"""Generate the fixed HuggingFace tokenizer and ONNX reference for V0.21.

The script is deliberately independent of the C# tokenizer. It uses the local
assets captured from the pinned HuggingFace revision and never downloads a
model or tokenizer.
"""

from __future__ import annotations

import hashlib
import json
import os
import platform
from pathlib import Path
from typing import Any

import numpy as np
import onnxruntime as ort
import sentencepiece
import transformers
from transformers import XLMRobertaTokenizer


EXPECTED_MODEL = "cross-encoder/mmarco-mMiniLMv2-L12-H384-v1"
EXPECTED_REVISION = "1427fd652930e4ba29e8149678df786c240d8825"
MAXIMUM_SEQUENCE_LENGTH = 192
MAXIMUM_QUERY_TOKENS = 48
MAXIMUM_SNIPPET_UTF16_UNITS = 200
LOGIT_ABSOLUTE_TOLERANCE = 1e-4

SCRIPT_PATH = Path(__file__).resolve()
REPOSITORY_ROOT = SCRIPT_PATH.parents[4]
MODEL_ROOT = (
    REPOSITORY_ROOT
    / "src"
    / "AIEverything.Desktop"
    / "Models"
    / "mmarco-mMiniLMv2-L12-H384-v1"
)
OUTPUT_PATH = Path(os.environ.get(
    "AIEVERYTHING_ONNX_REFERENCE_OUTPUT",
    SCRIPT_PATH.with_name("onnx-reference-v1427fd.json"),
)).resolve()


def read_json(path: Path) -> dict[str, Any]:
    return json.loads(path.read_text(encoding="utf-8-sig"))


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest().upper()


def truncate_utf16_surrogate_safe(value: str | None) -> str | None:
    if value is None or value.isspace() or value == "":
        return None

    encoded = value.encode("utf-16-le", errors="surrogatepass")
    encoded = encoded[: MAXIMUM_SNIPPET_UTF16_UNITS * 2]
    if len(encoded) >= 2:
        last_unit = int.from_bytes(encoded[-2:], "little")
        if 0xD800 <= last_unit <= 0xDBFF:
            encoded = encoded[:-2]
    return encoded.decode("utf-16-le", errors="strict")


def build_candidate_text(candidate: dict[str, Any]) -> str:
    prefix = f"{candidate['Name']}\n{candidate['FullPath']}"
    snippet = truncate_utf16_surrogate_safe(candidate.get("Snippet"))
    return prefix if snippet is None else f"{prefix}\n{snippet}"


def query_token_count(tokenizer: XLMRobertaTokenizer, query: str) -> int:
    count = len(tokenizer.encode(query, add_special_tokens=False))
    if count >= MAXIMUM_QUERY_TOKENS:
        raise ValueError(f"Query has {count} tokens and must remain below 48: {query!r}")
    return count


def encode_pair(
    tokenizer: XLMRobertaTokenizer,
    query: str,
    candidate_text: str,
) -> tuple[list[int], list[int]]:
    encoded = tokenizer(
        query,
        candidate_text,
        add_special_tokens=True,
        max_length=MAXIMUM_SEQUENCE_LENGTH,
        truncation="only_second",
        padding="max_length",
        return_attention_mask=True,
    )
    input_ids = [int(value) for value in encoded["input_ids"]]
    attention_mask = [int(value) for value in encoded["attention_mask"]]
    if len(input_ids) != MAXIMUM_SEQUENCE_LENGTH or len(attention_mask) != MAXIMUM_SEQUENCE_LENGTH:
        raise AssertionError("Every reference pair must be padded to 192 tokens.")
    return input_ids, attention_mask


def token_case(
    tokenizer: XLMRobertaTokenizer,
    case_id: str,
    query: str,
    candidate_text: str,
) -> dict[str, Any]:
    input_ids, attention_mask = encode_pair(tokenizer, query, candidate_text)
    return {
        "Id": case_id,
        "Query": query,
        "QueryTokenCount": query_token_count(tokenizer, query),
        "CandidateText": candidate_text,
        "InputIds": input_ids,
        "AttentionMask": attention_mask,
    }


def top_ten_candidates() -> list[dict[str, Any]]:
    return [
        {
            "Id": "c0",
            "Name": "annual-budget-2026.xlsx",
            "FullPath": r"D:\Finance\Budgets\annual-budget-2026.xlsx",
            "Snippet": "Approved annual budget plan for the Shanghai product launch in 2026.",
            "MatchSource": "both",
        },
        {
            "Id": "c1",
            "Name": "预算计划.md",
            "FullPath": r"D:\项目资料\上海发布\预算计划.md",
            "Snippet": "上海产品发布年度预算、里程碑与负责人确认记录。",
            "MatchSource": "content",
        },
        {
            "Id": "c2",
            "Name": "launch-notes.txt",
            "FullPath": r"C:\Users\TestUser\Documents\Launch\launch-notes.txt",
            "Snippet": "Q4 Shanghai launch notes include venue decisions but no approved budget.",
            "MatchSource": "both",
        },
        {
            "Id": "c3",
            "Name": "team-celebration🎉.jpg",
            "FullPath": r"D:\Photos\2026\team-celebration🎉.jpg",
            "Snippet": None,
            "MatchSource": "name",
        },
        {
            "Id": "c4",
            "Name": "WindowsUpdate.log",
            "FullPath": r"C:\Windows\Logs\WindowsUpdate\WindowsUpdate.log",
            "Snippet": "Update installation diagnostics and servicing events.",
            "MatchSource": "content",
        },
        {
            "Id": "c5",
            "Name": "node_modules",
            "FullPath": r"D:\projects\launch-dashboard\node_modules",
            "Snippet": None,
            "MatchSource": "name",
        },
        {
            "Id": "c6",
            "Name": "annual-report.docx",
            "FullPath": r"D:\Finance\Reports\annual-report.docx",
            "Snippet": "The annual report summarizes revenue, staffing, and prior-year expenses.",
            "MatchSource": "name",
        },
        {
            "Id": "c7",
            "Name": "budget-template.xlsx",
            "FullPath": r"D:\Finance\Templates\budget-template.xlsx",
            "Snippet": "Blank worksheet template for departmental estimates.",
            "MatchSource": "name",
        },
        {
            "Id": "c8",
            "Name": "上海发布预算😀.md",
            "FullPath": r"D:\项目资料\财务\上海发布预算😀.md",
            "Snippet": "中" * 199 + "😀tail that must be truncated before ONNX tokenization",
            "MatchSource": "both",
        },
        {
            "Id": "c9",
            "Name": "vacation-plan.md",
            "FullPath": r"D:\Personal\vacation-plan.md",
            "Snippet": "Ideas for a summer trip and packing checklist.",
            "MatchSource": "content",
        },
    ]


def main() -> None:
    calibration = read_json(MODEL_ROOT / "model-calibration.json")
    manifest = read_json(MODEL_ROOT / "model-manifest.json")
    if calibration.get("model") != EXPECTED_MODEL or manifest.get("model") != EXPECTED_MODEL:
        raise RuntimeError("The local model identity does not match the fixed reference model.")
    if calibration.get("revision") != EXPECTED_REVISION or manifest.get("revision") != EXPECTED_REVISION:
        raise RuntimeError("The local model assets are not from the fixed HuggingFace revision.")
    if calibration.get("tokenizer") != "XLMRobertaTokenizer":
        raise RuntimeError("The fixed reference must use HuggingFace XLMRobertaTokenizer.")

    tokenizer = XLMRobertaTokenizer.from_pretrained(MODEL_ROOT, local_files_only=True)
    token_cases = [
        token_case(
            tokenizer,
            "english",
            "annual budget",
            "2026 annual budget report\nD:\\work\\finance\\annual-budget-2026.xlsx",
        ),
        token_case(
            tokenizer,
            "chinese",
            "查找年度预算",
            "年度预算方案.md\nD:\\项目资料\\财务\\年度预算方案.md\n2026年预算与里程碑。",
        ),
        token_case(
            tokenizer,
            "windows_path",
            "project launch",
            "launch-plan.docx\nC:\\Users\\Wade\\Documents\\Q4\\launch-plan.docx",
        ),
        token_case(
            tokenizer,
            "emoji",
            "team celebration 🎉",
            "team-celebration🎉.jpg\nD:\\Photos\\2026\\team-celebration🎉.jpg",
        ),
        token_case(
            tokenizer,
            "long_candidate",
            "查找年度预算路径",
            build_candidate_text(
                {
                    "Name": "年度预算😀.md",
                    "FullPath": r"D:\项目资料\财务\年度预算😀.md",
                    "Snippet": "中" * 199 + "😀tail",
                }
            ),
        ),
    ]

    query = "find the annual budget plan for the Shanghai launch"
    candidates = top_ten_candidates()
    for candidate in candidates:
        candidate["CandidateText"] = build_candidate_text(candidate)
        if candidate["MatchSource"] in candidate["CandidateText"]:
            raise AssertionError("MatchSource must never enter the ONNX candidate text.")

    encoded_pairs = [encode_pair(tokenizer, query, candidate["CandidateText"]) for candidate in candidates]
    input_ids = np.asarray([pair[0] for pair in encoded_pairs], dtype=np.int64)
    attention_mask = np.asarray([pair[1] for pair in encoded_pairs], dtype=np.int64)

    options = ort.SessionOptions()
    options.execution_mode = ort.ExecutionMode.ORT_SEQUENTIAL
    options.inter_op_num_threads = 1
    options.intra_op_num_threads = min(4, max(1, (os.cpu_count() or 1) // 2))
    session = ort.InferenceSession(
        str(MODEL_ROOT / "model_quint8_avx2.onnx"),
        sess_options=options,
        providers=["CPUExecutionProvider"],
    )
    logits = session.run(
        ["logits"],
        {"input_ids": input_ids, "attention_mask": attention_mask},
    )[0].reshape(-1)
    if logits.shape != (10,) or not np.isfinite(logits).all():
        raise AssertionError(f"Expected ten finite logits, received shape {logits.shape}.")
    stable_descending = np.argsort(-logits, kind="stable")

    reference = {
        "SchemaVersion": 1,
        "Model": EXPECTED_MODEL,
        "Revision": EXPECTED_REVISION,
        "ModelSha256": sha256(MODEL_ROOT / "model_quint8_avx2.onnx"),
        "Tokenizer": "transformers.XLMRobertaTokenizer",
        "PairLayout": "<s> query </s></s> candidate </s>",
        "MaximumSequenceLength": MAXIMUM_SEQUENCE_LENGTH,
        "MaximumQueryTokens": MAXIMUM_QUERY_TOKENS,
        "SnippetMaximumUtf16Units": MAXIMUM_SNIPPET_UTF16_UNITS,
        "LogitAbsoluteTolerance": LOGIT_ABSOLUTE_TOLERANCE,
        "Python": {
            "Version": platform.python_version(),
            "OnnxRuntimeVersion": ort.__version__,
            "TransformersVersion": transformers.__version__,
            "SentencePieceVersion": sentencepiece.__version__,
            "NumpyVersion": np.__version__,
        },
        "TokenCases": token_cases,
        "Top10": {
            "Query": query,
            "QueryTokenCount": query_token_count(tokenizer, query),
            "Candidates": candidates,
            "InputIds": input_ids.tolist(),
            "AttentionMask": attention_mask.tolist(),
            "Logits": [float(value) for value in logits],
            "Ranking": [candidates[int(index)]["Id"] for index in stable_descending],
        },
    }
    OUTPUT_PATH.write_text(
        json.dumps(reference, ensure_ascii=False, indent=2) + "\n",
        encoding="utf-8",
        newline="\n",
    )
    print(f"Wrote {OUTPUT_PATH}")
    print("Top10 ranking:", ", ".join(reference["Top10"]["Ranking"]))


if __name__ == "__main__":
    main()
