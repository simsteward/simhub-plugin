# Gemini 2.5 Pro in Cursor

Cursor supports **Gemini 2.5 Pro** via the full model list; it is often not shown in the default model picker. This doc explains how to get it listed so you can select it when using a Google AI Studio API key.

## Prerequisites

- A [Google AI Studio](https://aistudio.google.com/) API key (same key used for Gemini 3.1 and other Gemini models).
- The key added and verified in **Cursor Settings → Models** under the Google provider.

## Model identifiers

Use one of these when adding the model in Cursor:

| Identifier | Notes |
|------------|--------|
| `gemini-2.5-pro-exp` | Cursor staff confirmed this is tied to the same backend as the 06-05 preview. |
| `gemini-2.5-pro-preview-06-05` | Date-stamped preview; Cursor may show a different date in your build. |

Check the full model list in Cursor for the exact name shown in your version.

## Steps to add Gemini 2.5 Pro

1. **Open Cursor Settings → Models**  
   (File → Preferences → Settings, then Models / AI providers.)

2. **Confirm your Google API key**  
   Under the Google provider, ensure your key is set and verified.

3. **Add Gemini 2.5 Pro from the full model list**  
   Cursor staff: *"You can also fetch from the Model list the Pro directly and add it to your list if it's not listed."*  
   - In the Models section, look for **"Add model"**, **"Fetch from model list"**, or a search/browse that shows more than the default set.  
   - Add the model using one of the identifiers above (use the one that appears in your Cursor build).

4. **If your build has "hidden" models**  
   Check for a toggle or "Show hidden models" and enable any Gemini 2.5 Pro entry that is hidden by default.

5. **If the model still doesn’t appear**  
   - Update Cursor to the latest version (validation fixes for custom model names were mentioned for 0.47.8+).  
   - Ask in the [Cursor Forum – Gemini 2.5 Pro not appearing](https://forum.cursor.com/t/gemini-2-5-pro-not-appearing-in-model-list/101046) or open a support request for the current identifier and UI location of "fetch from model list".

## References

- [Cursor – Models & Pricing](https://cursor.com/docs/models-and-pricing.md)
- [Cursor Forum: Gemini 2.5 Pro not appearing in model list](https://forum.cursor.com/t/gemini-2-5-pro-not-appearing-in-model-list/101046)
- [Cursor Forum: Gemini 2.5 pro models in Cursor v5.0](https://forum.cursor.com/t/gemini-2-5-pro-models-in-cursor-v5-0/91078)
