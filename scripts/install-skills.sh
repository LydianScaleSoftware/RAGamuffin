#!/bin/zsh
# Links RAGamuffin's Claude Code skills into ~/.claude/skills, so they're available in every project and follow the
# repo when it's updated.
set -euo pipefail
repo=${0:A:h:h}
target=~/.claude/skills
mkdir -p $target
for skill in $repo/skills/*(/); do
  name=${skill:t}
  if [[ -e $target/$name && ! -L $target/$name ]]; then
    echo "skip $name: $target/$name exists and isn't a link"
    continue
  fi
  ln -sfn $skill $target/$name
  echo "linked $name"
done
