# A fake cross-encoder

A 276-byte ONNX model with the same inputs and output as `ms-marco-MiniLM-L6-v2`
(`input_ids`, `attention_mask`, `token_type_ids` in, `logits` of shape `[batch, 1]` out). Its
"relevance score" is the number of passage tokens, counted from the token type IDs and the
attention mask, so a test can check that `CrossEncoderReranker` builds `[CLS] query [SEP] passage [SEP]`
pairs, marks the passage with token type 1, and pads a batch correctly, without downloading the
real 90 MB model. The live tests in CI use the real one (scripts/get-reranker.sh).
