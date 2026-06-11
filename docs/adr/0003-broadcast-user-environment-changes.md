# Broadcast user environment changes after initialization

The SDK switching tool writes user-level environment variables and managed PATH entries during initialization. After those writes it broadcasts the Windows environment change message so newly opened shells, IDEs, and Explorer-launched processes can observe the updated user environment, while still telling users that already-open terminals must be restarted.
