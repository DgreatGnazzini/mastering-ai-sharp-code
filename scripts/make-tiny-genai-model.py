"""Builds the 76 KB ONNX Runtime GenAI model the chapter 15 unit tests run (tests/PriorAuthBot.Tests/LocalModels/tiny-genai-model).

    pip install onnx tokenizers numpy
    python3 scripts/make-tiny-genai-model.py tests/PriorAuthBot.Tests/LocalModels/tiny-genai-model

Everything a real model folder has is real: genai_config.json, an ONNX decoder with a KV cache, a
byte-level BPE tokenizer.json, and a chat template in tokenizer_config.json. Only the "weights" are
fake. The logits come from a lookup table that maps each token to the next token of one fixed answer,
so greedy decoding always produces "The tiny model answered from source [1]." and then <|end|>.
That is enough to test, on every CI platform, that the native library loads, the chat template is
applied, tokens stream, and usage is counted, without downloading gigabytes of weights.

After rebuilding, update the file hashes in models.lock.json (entry priorauthbot-tiny-test).
"""
import json, os, sys
import numpy as np
import onnx
from onnx import helper, TensorProto, numpy_helper
from tokenizers import Tokenizer, models, pre_tokenizers, decoders

out = sys.argv[1]
os.makedirs(out, exist_ok=True)

# Byte-level vocabulary: the 256 byte symbols GPT-2 uses, then the answer's words, then specials.
bs = list(range(ord("!"), ord("~") + 1)) + list(range(ord("¡"), ord("¬") + 1)) + list(range(ord("®"), ord("ÿ") + 1))
cs = bs[:]
n = 0
for b in range(256):
    if b not in bs:
        bs.append(b); cs.append(256 + n); n += 1
byte_symbols = [chr(c) for c in cs]
answer = ["The", "Ġtiny", "Ġmodel", "Ġanswered", "Ġfrom", "Ġsource", "Ġ[1].", "<|end|>"]
specials = ["<|system|>", "<|user|>", "<|assistant|>", "<|end|>", "<s>", "</s>", "<unk>"]
vocab = {}
for s in specials + byte_symbols + answer:
    if s not in vocab:
        vocab[s] = len(vocab)
V = len(vocab)
tok = Tokenizer(models.BPE(vocab=vocab, merges=[], unk_token="<unk>"))
tok.pre_tokenizer = pre_tokenizers.ByteLevel(add_prefix_space=False)
tok.decoder = decoders.ByteLevel()
tok.add_special_tokens(specials)
tok.save(os.path.join(out, "tokenizer.json"))
template = ("{% for message in messages %}{{ '<|' + message['role'] + '|>' + message['content'] + '<|end|>' }}{% endfor %}"
            "{% if add_generation_prompt %}{{ '<|assistant|>' }}{% endif %}")
json.dump({"bos_token": "<s>", "eos_token": "<|end|>", "unk_token": "<unk>", "pad_token": "<unk>",
           "model_max_length": 1000000, "tokenizer_class": "GPT2Tokenizer", "chat_template": template,
           "add_bos_token": False, "clean_up_tokenization_spaces": False},
          open(os.path.join(out, "tokenizer_config.json"), "w"), indent=2)

# next(token): every token leads into the answer; each answer token leads to the next one.
chain = ["<|assistant|>"] + answer
nxt = np.full(V, vocab[answer[0]], dtype=np.int64)
for a, b in zip(chain, chain[1:]):
    nxt[vocab[a]] = vocab[b]
table = np.zeros((V, V), dtype=np.uint8)
table[np.arange(V), nxt] = 1

H, D = 1, 2  # one KV head of size 2: the cache exists, but carries nothing
emb = np.zeros((V, H * D), dtype=np.float32)
nodes = [
    helper.make_node("Gather", ["table", "input_ids"], ["g"]),
    helper.make_node("Cast", ["g"], ["logits"], to=TensorProto.FLOAT),
    helper.make_node("Gather", ["emb", "input_ids"], ["e"]),
    helper.make_node("Shape", ["input_ids"], ["ids_shape"]),
    helper.make_node("Concat", ["ids_shape", "hd"], ["kv_shape4"], axis=0),
    helper.make_node("Reshape", ["e", "kv_shape4"], ["e4"]),
    helper.make_node("Transpose", ["e4"], ["k"], perm=[0, 2, 1, 3]),
    helper.make_node("Concat", ["past_key_values.0.key", "k"], ["present.0.key"], axis=2),
    helper.make_node("Concat", ["past_key_values.0.value", "k"], ["present.0.value"], axis=2),
]
inits = [numpy_helper.from_array(table, "table"), numpy_helper.from_array(emb, "emb"),
         numpy_helper.from_array(np.array([H, D], dtype=np.int64), "hd")]
kv = lambda name, seq: helper.make_tensor_value_info(name, TensorProto.FLOAT, ["batch", H, seq, D])
graph = helper.make_graph(nodes, "tiny", 
    [helper.make_tensor_value_info("input_ids", TensorProto.INT64, ["batch", "seq"]),
     helper.make_tensor_value_info("attention_mask", TensorProto.INT64, ["batch", "total"]),
     kv("past_key_values.0.key", "past"), kv("past_key_values.0.value", "past")],
    [helper.make_tensor_value_info("logits", TensorProto.FLOAT, ["batch", "seq", V]),
     kv("present.0.key", "total"), kv("present.0.value", "total")], inits)
model = helper.make_model(graph, opset_imports=[helper.make_opsetid("", 17)], producer_name="priorauthbot-tiny")
model.ir_version = 8
onnx.checker.check_model(model)
onnx.save(model, os.path.join(out, "model.onnx"))

json.dump({"model": {"bos_token_id": vocab["<s>"], "eos_token_id": vocab["<|end|>"], "pad_token_id": vocab["<unk>"],
    "context_length": 65536, "vocab_size": V, "type": "llama",
    "decoder": {"session_options": {"log_id": "onnxruntime-genai", "provider_options": []}, "filename": "model.onnx",
        "head_size": D, "hidden_size": H * D, "num_attention_heads": H, "num_hidden_layers": 1, "num_key_value_heads": H,
        "inputs": {"input_ids": "input_ids", "attention_mask": "attention_mask",
                   "past_key_names": "past_key_values.%d.key", "past_value_names": "past_key_values.%d.value"},
        "outputs": {"logits": "logits", "present_key_names": "present.%d.key", "present_value_names": "present.%d.value"}}},
    "search": {"diversity_penalty": 0.0, "do_sample": False, "early_stopping": True, "length_penalty": 1.0,
        "max_length": 65536, "min_length": 0, "no_repeat_ngram_size": 0, "num_beams": 1, "num_return_sequences": 1,
        "past_present_share_buffer": False, "repetition_penalty": 1.0, "temperature": 1.0, "top_k": 1, "top_p": 1.0}},
    open(os.path.join(out, "genai_config.json"), "w"), indent=2)
print("vocab", V)
