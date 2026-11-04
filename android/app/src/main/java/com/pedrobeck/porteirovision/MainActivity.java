package com.pedrobeck.porteirovision;

import android.app.AlertDialog;
import android.content.Context;
import android.content.SharedPreferences;
import android.graphics.Bitmap;
import android.net.http.SslError;
import android.os.Build;
import android.os.Bundle;
import android.view.View;
import android.view.WindowInsets;
import android.view.WindowInsetsController;
import android.view.WindowManager;
import android.webkit.SslErrorHandler;
import android.webkit.WebChromeClient;
import android.webkit.WebResourceError;
import android.webkit.WebResourceRequest;
import android.webkit.WebSettings;
import android.webkit.WebView;
import android.webkit.WebViewClient;
import android.widget.EditText;
import android.widget.ProgressBar;
import android.widget.Toast;

import androidx.appcompat.app.AppCompatActivity;

public class MainActivity extends AppCompatActivity {

    private static final String PREFS_NAME = "PorteiroPrefs";
    private static final String KEY_SERVER_URL = "server_url";
    private static final String DEFAULT_LOCAL_URL = "http://192.168.4.127:8080";
    private static final String CLOUD_FALLBACK_URL = "https://porteiro-vision.vercel.app";

    private WebView webView;
    private ProgressBar loadingBar;
    private SharedPreferences prefs;
    private boolean triedFallback = false;

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);

        // 1. Mantém a tela SEMPRE acesa (Kiosk / Monitor de Parede)
        getWindow().addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);

        setContentView(R.layout.activity_main);

        prefs = getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE);
        webView = findViewById(R.id.webView);
        loadingBar = findViewById(R.id.loadingBar);

        // 2. Oculta barras de navegação do Android (Tela Cheia Imersiva)
        applyImmersiveFullscreen();

        // 3. Configura WebView com máxima aceleração de hardware
        setupWebView();

        // 4. Carrega a URL do servidor
        loadConfiguredUrl();
    }

    @Override
    protected void onResume() {
        super.onResume();
        applyImmersiveFullscreen();
    }

    private void applyImmersiveFullscreen() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
            final WindowInsetsController insetsController = getWindow().getInsetsController();
            if (insetsController != null) {
                insetsController.hide(WindowInsets.Type.statusBars() | WindowInsets.Type.navigationBars());
                insetsController.setSystemBarsBehavior(WindowInsetsController.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE);
            }
        } else {
            getWindow().getDecorView().setSystemUiVisibility(
                    View.SYSTEM_UI_FLAG_LAYOUT_STABLE
                            | View.SYSTEM_UI_FLAG_LAYOUT_HIDE_NAVIGATION
                            | View.SYSTEM_UI_FLAG_LAYOUT_FULLSCREEN
                            | View.SYSTEM_UI_FLAG_HIDE_NAVIGATION
                            | View.SYSTEM_UI_FLAG_FULLSCREEN
                            | View.SYSTEM_UI_FLAG_IMMERSIVE_STICKY
            );
        }
    }

    private void setupWebView() {
        WebSettings settings = webView.getSettings();
        settings.setJavaScriptEnabled(true);
        settings.setDomStorageEnabled(true);
        settings.setDatabaseEnabled(true);
        settings.setMediaPlaybackRequiresUserGesture(false);
        settings.setUseWideViewPort(true);
        settings.setLoadWithOverviewMode(true);
        settings.setBuiltInZoomControls(false);
        settings.setDisplayZoomControls(false);

        // Permite carregar HTTP e HTTPS juntos (câmeras locais + nuvem)
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.LOLLIPOP) {
            settings.setMixedContentMode(WebSettings.MIXED_CONTENT_ALWAYS_ALLOW);
        }

        // Aceleração de hardware gráfica
        webView.setLayerType(View.LAYER_TYPE_HARDWARE, null);

        webView.setWebChromeClient(new WebChromeClient() {
            @Override
            public void onProgressChanged(WebView view, int newProgress) {
                if (newProgress >= 90) {
                    loadingBar.setVisibility(View.GONE);
                }
            }
        });

        webView.setWebViewClient(new WebViewClient() {
            @Override
            public void onPageStarted(WebView view, String url, Bitmap favicon) {
                loadingBar.setVisibility(View.VISIBLE);
            }

            @Override
            public void onPageFinished(WebView view, String url) {
                loadingBar.setVisibility(View.GONE);
            }

            @Override
            public void onReceivedError(WebView view, WebResourceRequest request, WebResourceError error) {
                if (request.isForMainFrame()) {
                    handleConnectionError();
                }
            }

            @Override
            public void onReceivedSslError(WebView view, SslErrorHandler handler, SslError error) {
                handler.proceed(); // Permite certificados auto-assinados de câmeras
            }
        });

        // Toque longo com dois dedos para abrir o menu de trocar o IP do servidor
        webView.setOnLongClickListener(v -> {
            showConfigServerDialog();
            return true;
        });
    }

    private void loadConfiguredUrl() {
        String url = prefs.getString(KEY_SERVER_URL, DEFAULT_LOCAL_URL);
        triedFallback = false;
        webView.loadUrl(url);
    }

    private void handleConnectionError() {
        if (!triedFallback) {
            triedFallback = true;
            Toast.makeText(this, "Rede local inacessível. Conectando via Nuvem...", Toast.LENGTH_SHORT).show();
            webView.loadUrl(CLOUD_FALLBACK_URL);
        } else {
            loadingBar.setVisibility(View.GONE);
            Toast.makeText(this, "Não foi possível conectar. Segure na tela para alterar o IP.", Toast.LENGTH_LONG).show();
        }
    }

    private void showConfigServerDialog() {
        AlertDialog.Builder builder = new AlertDialog.Builder(this);
        builder.setTitle("Configurar IP do Servidor");

        final EditText input = new EditText(this);
        String currentUrl = prefs.getString(KEY_SERVER_URL, DEFAULT_LOCAL_URL);
        input.setText(currentUrl);
        builder.setView(input);

        builder.setPositiveButton("Salvar e Conectar", (dialog, which) -> {
            String newUrl = input.getText().toString().trim();
            if (!newUrl.isEmpty()) {
                if (!newUrl.startsWith("http://") && !newUrl.startsWith("https://")) {
                    newUrl = "http://" + newUrl;
                }
                prefs.edit().putString(KEY_SERVER_URL, newUrl).apply();
                triedFallback = false;
                webView.loadUrl(newUrl);
                Toast.makeText(MainActivity.this, "Conectando em " + newUrl, Toast.LENGTH_SHORT).show();
            }
        });

        builder.setNeutralButton("Nuvem (Vercel)", (dialog, which) -> {
            prefs.edit().putString(KEY_SERVER_URL, CLOUD_FALLBACK_URL).apply();
            triedFallback = false;
            webView.loadUrl(CLOUD_FALLBACK_URL);
        });

        builder.setNegativeButton("Cancelar", (dialog, which) -> dialog.cancel());
        builder.show();
    }

    @Override
    public void onBackPressed() {
        if (webView.canGoBack()) {
            webView.goBack();
        } else {
            // Em modo Kiosk, evita fechar sem querer
            Toast.makeText(this, "Pressione Voltar novamente para sair", Toast.LENGTH_SHORT).show();
            super.onBackPressed();
        }
    }
}
