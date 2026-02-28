#!/bin/bash
GITIGNORE=".gitignore"
touch $GITIGNORE
add_if_missing() {
    grep -qxF "$1" $GITIGNORE || echo "$1" >> $GITIGNORE
}
add_if_missing "# Publish output"
add_if_missing "publish/"
add_if_missing "*.zip"
add_if_missing "# ASP.NET local secrets"
add_if_missing "*.secrets.json"
add_if_missing "# macOS / Windows system files"
add_if_missing ".DS_Store"
add_if_missing "Thumbs.db"
add_if_missing "# Logs"
add_if_missing "*.log"
add_if_missing "# Future-proof secret patterns"
add_if_missing "*creds*.json"
add_if_missing "*secret*.json"
echo ".gitignore updated successfully."
