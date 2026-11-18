import os
import uvicorn

if __name__ == "__main__":
    uvicorn.run("vatican_shop.presentation.app:create_app", factory=True,
                host="0.0.0.0", port=int(os.getenv("VD_PORT", "8080")), proxy_headers=False)
