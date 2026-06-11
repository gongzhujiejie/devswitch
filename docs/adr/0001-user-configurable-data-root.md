# Use a user-configurable data root with portable detection

The SDK switching tool stores configuration, downloads, managed SDKs, current links, and logs under a data root rather than hard-coding a C: drive path. By default it uses `%LOCALAPPDATA%\SdkSwitch`, allows the user to choose another writable directory during first launch or later migration, and automatically uses an executable-adjacent `data\` directory when present so portable deployments work without changing the normal installed-app experience.
