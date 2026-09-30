# Turian launchers

The distribution launcher shared by `turian-studio` and `turian-cli`. Loads the appropriate entry assembly from `lib/` and forwards command-line arguments and exit codes.

Published builds use this project to keep application libraries separate from the executable. Authoring logic lives in [Editor Core](../Core/).
