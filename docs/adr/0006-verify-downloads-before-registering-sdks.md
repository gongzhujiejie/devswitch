# Verify downloads before registering SDKs

DevSwitch supports official sources and mirror sources for online SDK downloads. Downloaded archives must be verified with SHA256 or signature information before extraction and registration, because mirrors are treated as transport accelerators rather than sources of trust; failed verification leaves the archive unregistered and prevents it from becoming a selectable SDK version.
