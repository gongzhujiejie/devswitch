# Use Junction links for current SDK entries

The SDK switching tool uses stable `current` entries as the switching surface, and points each entry at the selected SDK root. Directory Junctions are the default because they work well for user-level Windows switching without requiring administrator rights or Developer Mode; symbolic links are only attempted as a fallback when Junction creation is not suitable.
