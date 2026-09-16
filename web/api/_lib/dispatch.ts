// Fires the private worker repo's publish workflow with only the job id. The worker then
// claims the encrypted payload over HMAC. Env: WORKER_REPO ("owner/name"), GITHUB_TOKEN
// (fine-grained PAT, Actions: write on that repo only).
export async function dispatchWorker(jobId: string): Promise<void> {
  const repo = process.env.WORKER_REPO;
  const token = process.env.GITHUB_TOKEN;
  if (!repo || !token) throw new Error("WORKER_REPO / GITHUB_TOKEN not configured");

  const res = await fetch(
    `https://api.github.com/repos/${repo}/actions/workflows/publish.yml/dispatches`,
    {
      method: "POST",
      headers: {
        authorization: `Bearer ${token}`,
        accept: "application/vnd.github+json",
        "x-github-api-version": "2022-11-28",
        "content-type": "application/json",
      },
      body: JSON.stringify({ ref: "main", inputs: { job_id: jobId } }),
    },
  );
  if (!res.ok) {
    const detail = await res.text().catch(() => "");
    throw new Error(`workflow_dispatch failed: ${res.status} ${detail}`);
  }
}
