# Use a hidden helper executable for Windows system operations

The first version is GUI-first, but Windows operations such as Junction creation, user environment writes, managed SDK deletion, and environment-change broadcasting are performed by a hidden C++ helper executable rather than a directly loaded DLL. This slightly increases call overhead, but isolates failures from the WinUI process and leaves room for future elevation or diagnostics without exposing a command-line workflow to users.
