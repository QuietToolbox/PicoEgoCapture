// EgoActivity.java — 自定义 Unity 活动,拦截音量键 → 转发给 Unity(EgoRig.OnVolumeKey)。
package com.ego;

import android.view.KeyEvent;
import com.unity3d.player.UnityPlayer;
import com.unity3d.player.UnityPlayerActivity;

public class EgoActivity extends UnityPlayerActivity {
    @Override
    public boolean onKeyDown(int keyCode, KeyEvent event) {
        if (keyCode == KeyEvent.KEYCODE_VOLUME_UP || keyCode == KeyEvent.KEYCODE_VOLUME_DOWN) {
            if (event.getRepeatCount() == 0) {
                UnityPlayer.UnitySendMessage("EgoRig", "OnVolumeKey", "");
            }
            return true; // 消费掉,不改系统音量
        }
        return super.onKeyDown(keyCode, event);
    }
}
