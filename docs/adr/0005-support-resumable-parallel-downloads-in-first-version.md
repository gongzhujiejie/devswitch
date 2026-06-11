# Support resumable parallel downloads in the first version

The first version includes online SDK downloads, and those downloads should support parallel chunk downloading plus resume capability rather than simple full-file retry only. This increases downloader complexity, but matches the product goal of fast Windows-native SDK management and avoids forcing users to restart large JDK, Node.js, or Go downloads after transient network failures.
