# theme-management

テーマ稼働管理システム

## 認証 / Authentication

認証は **Azure App Service Easy Auth + Entra ID** で行います。アプリケーションコード内に認証ロジックはなく、Azure インフラ側で処理されます。

Easy Auth が有効化された App Service 上では、未認証リクエストは Entra ID ログインページへ自動的にリダイレクトされます。

## 認可 / Authorization (RBAC)

認証後のロールベースアクセス制御はアプリケーション側で実装しています。

### ロール設定

Entra ID のアプリ登録で App Role を定義し、ユーザーまたはグループをロールに割り当ててください。

| ロール | 権限 |
|--------|------|
| Admin  | 閲覧・編集・追加・削除すべて可能 |
| (ロール未割当) | 閲覧のみ可能（編集・追加・削除ボタン非表示） |

> **注**: ロールが未割当のユーザーもアプリにはアクセスできます（Easy Auth で認証済みのため）。ただし、編集・追加・削除の操作は Admin ロールのみに表示されます。完全にアクセスをブロックするには、Azure Portal のアプリ登録で「割り当てが必要」を有効化してください。

### Easy Auth の設定

Azure Portal の App Service → 認証 で以下を設定してください：

1. ID プロバイダー: Microsoft (Entra ID)
2. アクセスを制限する: 認証が必要
3. アプリ登録でアプリロール（例: `Admin`）を定義
4. ユーザーまたはグループをロールに割り当て

## 稼働予測エージェント

稼働予測エージェントは、Azure OpenAI または GitHub Copilot SDK を利用します。「来月アサイン可能なエンジニアを教えて」「テーマ〇〇に稼働余力があるエンジニアをアサインして」といった自然な日本語での問い合わせに対して、システムのデータを分析し、エンジニア配置を提案・実行します。利用する方式はアプリ内の「設定・管理」ページで選択できます。

### 前提条件

両方の方式を利用する場合は、方式ごとに次の準備が必要です。

- **Azure OpenAI**: アプリケーション実行環境に Azure CLI をインストールし、Azure OpenAI にアクセスできるアカウントで認証してください。指定したモデル名のデプロイも必要です。
- **GitHub Copilot SDK**: SDK が利用する Copilot CLI ランタイムは NuGet パッケージの復元時に取得されます。実行環境で GitHub CLI にサインインし、Copilot を利用できるアカウントを使用してください。

### Azure OpenAI のセットアップ

#### 1. Azure CLI をインストール

インストール手順は [Azure CLI の公式ドキュメント](https://learn.microsoft.com/cli/azure/install-azure-cli) を参照してください。

#### 2. Azure にログイン

```bash
az login
az account set --subscription <subscription-id>
```

アプリケーションを実行するホストで認証してください。Azure OpenAI にアクセスできるアカウントとサブスクリプションを選択します。

#### 3. 環境変数を設定

Azure OpenAI を利用するには、アプリケーションの実行環境に次の環境変数が必要です。未設定の場合、Azure OpenAI 方式のエージェントは起動できません。

| 環境変数 | 内容 |
|----------|------|
| `AZURE_OPENAI_ENDPOINT` | Azure OpenAI のエンドポイント URL |
| `AZURE_OPENAI_TOKEN_SCOPE` | トークン取得時のスコープ（例: `api://<app-id>/.default`） |

```powershell
$env:AZURE_OPENAI_ENDPOINT = "https://<your-endpoint>/"
$env:AZURE_OPENAI_TOKEN_SCOPE = "api://<app-id>/.default"
```

### GitHub Copilot SDK のセットアップ

アプリケーション実行環境で GitHub CLI にログインし、Copilot 利用権限を確認してください。

```bash
gh auth login
gh auth refresh --hostname github.com --scopes copilot
```

Copilot CLI ランタイムは `GitHub.Copilot.SDK` NuGet パッケージに含まれ、ビルドまたはパッケージ復元時に取得されます。実行環境から GitHub への接続が必要です。

### 設定

「設定・管理」ページで実行方式とモデル名を選択し、保存してください。Azure OpenAI を選ぶ場合は Azure 上のデプロイ名、GitHub Copilot SDK を選ぶ場合は Copilot で利用できるモデル名を指定します。初期値は Azure OpenAI と `gpt-5-mini` です。

アプリ内で保存した設定は `ThemeManagement/copilot-agent-settings.json` に保存されます。初期値やモデル選択肢は `ThemeManagement/appsettings.json` の `CopilotAgent` セクションで設定します。変更は次回のセッション作成時から反映されます。

```json
{
  "CopilotAgent": {
    "Provider": "AzureOpenAI",
    "Model": "gpt-5-mini",
    "AvailableModels": [
      "gpt-5-mini",
      "gpt-5",
      "gpt-4.1",
      "gpt-4o",
      "claude-sonnet-4.5",
      "gpt-6-luna"
    ]
  }
}
```

### 利用方法

1. アプリにログインした状態で「稼働予測エージェント」ページに遷移してください
2. テキスト入力欄にエンジニア配置に関する質問や指示を日本語で入力します
   - 例1: 「来月アサイン可能なエンジニアを教えて」
   - 例2: 「テーマ A に稼働余力があるエンジニアを教えて」
   - 例3: 「テーマ B にエンジニア C を 50 時間アサインして」
3. **Enter** キーを押すか、送信ボタンをクリックします
4. エージェントが提案・実行結果を表示します

### トラブルシューティング

**「エージェントの起動に失敗しました」エラーが表示される場合:**

- Azure CLI がインストールされているか確認してください（`az --version`）
- Azure にログインし、正しいサブスクリプションを選択しているか確認してください（`az account show`）
- ログイン中のアカウントが、設定された Azure OpenAI サービスとモデルのデプロイにアクセスできるか確認してください
- GitHub Copilot SDK を選択している場合は、実行環境の GitHub CLI 認証と Copilot 利用権限を確認してください（`gh auth status`）

**エージェントが応答しない場合:**

- ネットワーク接続を確認してください
- 選択したプロバイダーのサービス状態と、設定したモデル名が正しいか確認してください
- ブラウザのコンソール（F12）を確認してください
