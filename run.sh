#!/bin/bash
VIDEO=$(osascript -e 'POSIX path of (choose file with prompt "Select a video file:")')
if [ -z "$VIDEO" ]; then
  echo "No file selected."
  exit 1
fi
dotnet run --project /Users/c.to.the.d/test/MeterReaderApp -- "$VIDEO"
