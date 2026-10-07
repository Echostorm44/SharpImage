// twin_part_cnn.inc (included at the end of twin_part.c, so partition_strategy.c's statics are visible):
// the intra CNN partition pruner and the ML partition-search breakout.

// av1_cnn_predict_img_multi_out(_highbd) with the partition CNN, as intra_mode_cnn_partition calls it; src at (-1,-1)
EXPORT int twin_cnn_predict(const void *src, int stride, int hbd, int bd, float *out) {
  const int out_chs[4] = { CNN_BRANCH_0_OUT_CH, CNN_BRANCH_1_OUT_CH, CNN_BRANCH_2_OUT_CH, CNN_BRANCH_3_OUT_CH };
  const int output_dims[4] = { 1, 2, 4, 8 };
  float *output_buffer[CNN_TOT_OUT_CH];
  float **cur = output_buffer;
  float *p = out;
  for (int o = 0; o < 4; o++) {
    for (int ch = 0; ch < out_chs[o]; ch++) { cur[ch] = p; p += output_dims[o] * output_dims[o]; }
    cur += out_chs[o];
  }
  CNN_MULTI_OUT output = { .num_outputs = 4, .output_channels = out_chs, .output_strides = output_dims,
                           .output_buffer = output_buffer };
  const CNN_THREAD_DATA thread_data = { .num_workers = 1, .workers = NULL };
  bool ok;
  if (hbd) {
    uint16_t *image[1] = { (uint16_t *)src };
    ok = av1_cnn_predict_img_multi_out_highbd(image, 65, 65, stride, &av1_intra_mode_cnn_partition_cnn_config,
                                              &thread_data, bd, &output);
  } else {
    uint8_t *image[1] = { (uint8_t *)src };
    ok = av1_cnn_predict_img_multi_out(image, 65, 65, stride, &av1_intra_mode_cnn_partition_cnn_config, &thread_data,
                                       &output);
  }
  return ok;
}

// intra_mode_cnn_partition: blk points at the block's top-left (the 64x64 superblock's for bsize 64), with the row
// above / column left readable. state: [partition_none_allowed, do_square_split, do_rectangular_split, rect_allowed
// HORZ, VERT] in/out. reset: clear cnn_output_valid first. out: [cnn_buffer (1636), log_q, cnn_output_valid].
EXPORT void twin_intra_cnn_partition(const void *blk, int stride, int hbd, int bd, int qindex, int bsize,
                                     int quad_tree_idx, int frame_w, int frame_h, int prune_level, int reset,
                                     int *state, float *out) {
  setup(bd, hbd);
  cpi_->common.width = frame_w; cpi_->common.height = frame_h;
  x_->qindex = qindex;
  x_->plane[0].src.buf = hbd ? CONVERT_TO_BYTEPTR((uint16_t *)blk) : (uint8_t *)blk;
  x_->plane[0].src.stride = stride;
  if (reset) x_->part_search_info.cnn_output_valid = 0;
  PartitionSearchState ps;
  memset(&ps, 0, sizeof(ps));
  ps.part_blk_params.bsize = (BLOCK_SIZE)bsize;
  ps.partition_none_allowed = state[0]; ps.do_square_split = state[1]; ps.do_rectangular_split = state[2];
  ps.partition_rect_allowed[HORZ] = state[3]; ps.partition_rect_allowed[VERT] = state[4];
  WITH_FPCW(intra_mode_cnn_partition(&cpi_->common, x_, quad_tree_idx, prune_level, &ps));
  state[0] = ps.partition_none_allowed; state[1] = ps.do_square_split; state[2] = ps.do_rectangular_split;
  state[3] = ps.partition_rect_allowed[HORZ]; state[4] = ps.partition_rect_allowed[VERT];
  memcpy(out, x_->part_search_info.cnn_buffer, CNN_OUT_BUF_SIZE * sizeof(float));
  out[CNN_OUT_BUF_SIZE] = x_->part_search_info.log_q;
  out[CNN_OUT_BUF_SIZE + 1] = (float)x_->part_search_info.cnn_output_valid;
}

// av1_ml_predict_breakout; state: [do_square_split, do_rectangular_split] in/out
EXPORT void twin_ml_breakout(int bsize, int rate, int64_t dist, unsigned int pb_source_variance, int rdmult, int qindex,
                             int bd, const float *thresh, int model_index, int level, int *state) {
  setup(bd, bd > 8);
  static int16_t deq[2];
  deq[0] = (int16_t)av1_dc_quant_QTX(qindex, 0, (aom_bit_depth_t)bd);
  deq[1] = deq[0];
  x_->plane[0].dequant_QTX = deq;
  x_->rdmult = rdmult;
  for (int i = 0; i < 5; i++) cpi_->sf.part_sf.ml_partition_search_breakout_thresh[i] = thresh[i];
  cpi_->sf.part_sf.ml_partition_search_breakout_model_index = model_index;
  cpi_->sf.part_sf.ml_predict_breakout_level = level;
  RD_STATS rd;
  av1_init_rd_stats(&rd);
  rd.rate = rate; rd.dist = dist;
  PartitionSearchState ps;
  memset(&ps, 0, sizeof(ps));
  ps.part_blk_params.bsize = (BLOCK_SIZE)bsize;
  ps.do_square_split = state[0]; ps.do_rectangular_split = state[1];
  WITH_FPCW(av1_ml_predict_breakout(cpi_, x_, &rd, pb_source_variance, bd, &ps));
  state[0] = ps.do_square_split; state[1] = ps.do_rectangular_split;
}

// (float)log(t / (1 - t)) as av1_ml_predict_breakout computes its threshold score, over float bit patterns
EXPORT void twin_log_ratio_bits(uint32_t start, uint32_t count, float *out) {
  unsigned short old = twin_fpcw_enter();
  for (uint32_t i = 0; i < count; i++) {
    float t; uint32_t u = start + i; memcpy(&t, &u, 4);
    out[i] = (float)log(t / (1 - t));
  }
  twin_fpcw_leave(old);
}
