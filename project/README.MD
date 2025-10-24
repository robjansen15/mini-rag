# Multi-file Pack

This pack includes three C# files, a Python utility, and this README. The C# files provide:

- A lock-free-style bounded channel over a ring buffer with async APIs.
- A small dependency-aware rule engine.
- A minimal CQRS/event-sourcing abstraction with snapshots.

The Python utility traverses directories, selects files by extension sets, and emits JSONL records with content and metadata.

## Build (C#)