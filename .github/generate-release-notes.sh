#!/usr/bin/env bash
set -euo pipefail

VERSION="${1:?version is required}"
OUTPUT="${2:?output file is required}"

if git rev-parse "${VERSION}" >/dev/null 2>&1; then
  BASE_REF="${VERSION}"
else
  BASE_REF="$(git tag --sort=-version:refname | head -n 1)"
fi

HEAD_REF="$(git rev-parse --short HEAD)"

{
  echo "# RelayForge ${VERSION}"
  echo
echo "## 更新内容"
  if [ -n "${BASE_REF}" ] && [ "$(git rev-list --count "${BASE_REF}..HEAD")" -gt 0 ]; then
    git log "${BASE_REF}..HEAD" --no-merges --pretty=format:'- %s (%h)'
  else
    echo "- 本次版本没有新增提交，更新的是构建产物或发布文件。"
  fi
  echo
  echo
  echo "## 变更范围"
  if [ -n "${BASE_REF}" ]; then
    git diff --stat "${BASE_REF}..HEAD" || true
  else
    git diff --stat HEAD~20..HEAD || true
  fi
  echo
  echo "构建提交：${HEAD_REF}"
} > "${OUTPUT}"
