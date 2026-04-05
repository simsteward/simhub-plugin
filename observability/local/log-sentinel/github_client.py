"""GitHub issue client — thin gh CLI wrapper for T4 sentinel filings."""

import json
import logging
import os
import subprocess

logger = logging.getLogger("sentinel.github")


class GitHubClient:
    def __init__(self, repo: str, token: str = ""):
        """
        repo: "owner/name" format, e.g. "simsteward/simhub-plugin"
        token: optional GH_TOKEN override; if empty, gh CLI uses its own auth
        """
        self.repo = repo
        self._env = {"GH_TOKEN": token} if token else None

    @property
    def enabled(self) -> bool:
        return bool(self.repo)

    def find_open_issue(self, fingerprint: str) -> dict | None:
        """Return {number, url, title} of first open issue with fingerprint label, or None."""
        label = f"fingerprint:{fingerprint}"
        try:
            result = subprocess.run(
                ["gh", "issue", "list", "--repo", self.repo,
                 "--label", label, "--state", "open",
                 "--json", "number,url,title", "--limit", "1"],
                capture_output=True, text=True, timeout=15,
                env=self._merged_env(),
            )
            if result.returncode != 0:
                logger.warning("gh issue list failed: %s", result.stderr[:200])
                return None
            issues = json.loads(result.stdout or "[]")
            return issues[0] if issues else None
        except Exception as e:
            logger.warning("find_open_issue error: %s", e)
            return None

    def create_issue(self, title: str, body: str, labels: list[str]) -> dict | None:
        """Create a new issue. Returns {number, url} or None on failure."""
        try:
            label_args = []
            for lbl in labels:
                label_args += ["--label", lbl]
            result = subprocess.run(
                ["gh", "issue", "create", "--repo", self.repo,
                 "--title", title, "--body", body] + label_args,
                capture_output=True, text=True, timeout=30,
                env=self._merged_env(),
            )
            if result.returncode != 0:
                logger.warning("gh issue create failed: %s", result.stderr[:200])
                return None
            url = result.stdout.strip()
            # Extract number from URL (last path segment)
            number = int(url.rstrip("/").split("/")[-1]) if url else 0
            return {"number": number, "url": url}
        except Exception as e:
            logger.warning("create_issue error: %s", e)
            return None

    def add_comment(self, issue_number: int, body: str) -> bool:
        """Add a comment to an existing issue. Returns True on success."""
        try:
            result = subprocess.run(
                ["gh", "issue", "comment", str(issue_number),
                 "--repo", self.repo, "--body", body],
                capture_output=True, text=True, timeout=15,
                env=self._merged_env(),
            )
            if result.returncode != 0:
                logger.warning("gh issue comment failed: %s", result.stderr[:200])
                return False
            return True
        except Exception as e:
            logger.warning("add_comment error: %s", e)
            return False

    def _merged_env(self) -> dict | None:
        """Return env dict with GH_TOKEN merged in, or None to inherit parent env."""
        if not self._env:
            return None
        merged = os.environ.copy()
        merged.update(self._env)
        return merged
